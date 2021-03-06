// Copyright 2020 Andrii "Ryzhehvost" Kotlyar
// Contact: ryzhehvost@kotei.co.ua
// This derivative work is based on ArchiWebHandler.cs from https://github.com/JustArchiNET/ArchiSteamFarm/tree/0ce04415
// -------------------------------------------------------------------------------------------------
// Copyright 2015-2020 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using AngleSharp.Dom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;

namespace SteamAuthNet {
	public sealed class WebHandler : IDisposable {

		public const string SteamCommunityURL = "https://" + SteamCommunityHost;

		public const string SteamHelpURL = "https://" + SteamHelpHost;

		public const string SteamStoreURL = "https://" + SteamStoreHost;

		internal const ushort MaxItemsInSingleInventoryRequest = 5000;

		private const string IEconService = "IEconService";
		private const string IPlayerService = "IPlayerService";
		private const string ISteamApps = "ISteamApps";
		private const string ISteamUserAuth = "ISteamUserAuth";
		private const string ITwoFactorService = "ITwoFactorService";
		private const string SteamCommunityHost = "steamcommunity.com";
		private const string SteamHelpHost = "help.steampowered.com";
		private const string SteamStoreHost = "store.steampowered.com";

		public string CachedApiKey {get; set;} = null;

		private static readonly ImmutableDictionary<string, (SemaphoreSlim RateLimitingSemaphore, SemaphoreSlim OpenConnectionsSemaphore)> WebLimitingSemaphores = new Dictionary<string, (SemaphoreSlim RateLimitingSemaphore, SemaphoreSlim OpenConnectionsSemaphore)>(4, StringComparer.Ordinal) {
			{ nameof(WebHandler), (new SemaphoreSlim(1, 1), new SemaphoreSlim(WebBrowser.MaxConnections, WebBrowser.MaxConnections)) },
			{ SteamCommunityURL, (new SemaphoreSlim(1, 1), new SemaphoreSlim(WebBrowser.MaxConnections, WebBrowser.MaxConnections)) },
			{ SteamHelpURL, (new SemaphoreSlim(1, 1), new SemaphoreSlim(WebBrowser.MaxConnections, WebBrowser.MaxConnections)) },
			{ SteamStoreURL, (new SemaphoreSlim(1, 1), new SemaphoreSlim(WebBrowser.MaxConnections, WebBrowser.MaxConnections)) },
			{ WebAPI.DefaultBaseAddress.Host, (new SemaphoreSlim(1, 1), new SemaphoreSlim(WebBrowser.MaxConnections, WebBrowser.MaxConnections)) }
		}.ToImmutableDictionary(StringComparer.Ordinal);

		public readonly WebBrowser WebBrowser;

		private readonly SemaphoreSlim SessionSemaphore = new SemaphoreSlim(1, 1);

		private bool Initialized;
		private DateTime LastSessionCheck;
		private DateTime LastSessionRefresh;
		private bool MarkingInventoryScheduled;
		private string VanityURL;
		public ulong SteamID { get; set; }

		internal WebHandler(IWebProxy WebProxy) {
			Task<(bool,string)> task = Task.Run(async () => await ResolveApiKey());
            (bool success, string ApiKey) = task.Result;
			if (success) {
				CachedApiKey = ApiKey;
			}
			WebBrowser = new WebBrowser(WebProxy);
		}

		public void Dispose() {
			SessionSemaphore.Dispose();
			WebBrowser.Dispose();
		}

		public async Task<string> GetAbsoluteProfileURL(bool waitForInitialization = true) {
			if (waitForInitialization && !Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {
					return null;
				}
			}

			return string.IsNullOrEmpty(VanityURL) ? "/profiles/" + SteamID : "/id/" + VanityURL;
		}

#pragma warning disable 1998
		public async Task<bool?> HasValidApiKey() {
			return !string.IsNullOrEmpty(CachedApiKey);
		}
#pragma warning restore 1998

		public async Task<IDocument> UrlGetToHtmlDocumentWithSession(string host, string request, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request)) {
				return null;
			}

			if (maxTries == 0) {
				return null;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlGetToHtmlDocumentWithSession(host, request, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {
					return null;
				}
			}

			WebBrowser.HtmlDocumentResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlGetToHtmlDocument(host + request).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlGetToHtmlDocumentWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {
				return await UrlGetToHtmlDocumentWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<T> UrlGetToJsonObjectWithSession<T>(string host, string request, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) where T : class {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request)) {
				return default;
			}

			if (maxTries == 0) {

				return default;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlGetToJsonObjectWithSession<T>(host, request, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return default;
				}
			}

			WebBrowser.ObjectResponse<T> response = await WebLimitRequest(host, async () => await WebBrowser.UrlGetToJsonObject<T>(host + request).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return default;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlGetToJsonObjectWithSession<T>(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlGetToJsonObjectWithSession<T>(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<XmlDocument> UrlGetToXmlDocumentWithSession(string host, string request, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request)) {

				return null;
			}

			if (maxTries == 0) {

				return null;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlGetToXmlDocumentWithSession(host, request, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			WebBrowser.XmlDocumentResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlGetToXmlDocument(host + request).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlGetToXmlDocumentWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlGetToXmlDocumentWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<bool> UrlHeadWithSession(string host, string request, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request)) {

				return false;
			}

			if (maxTries == 0) {

				return false;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlHeadWithSession(host, request, true, --maxTries).ConfigureAwait(false);
					}

					return false;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return false;
				}
			}

			WebBrowser.BasicResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlHead(host + request).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return false;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlHeadWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return false;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlHeadWithSession(host, request, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return true;
		}

		public async Task<IDocument> UrlPostToHtmlDocumentWithSession(string host, string request, Dictionary<string, string> data = null, string referer = null, ESession session = ESession.Lowercase, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request) || !Enum.IsDefined(typeof(ESession), session)) {

				return null;
			}

			if (maxTries == 0) {

				return null;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlPostToHtmlDocumentWithSession(host, request, data, referer, session, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			if (session != ESession.None) {
				string sessionID = WebBrowser.CookieContainer.GetCookieValue(host, "sessionid");

				if (string.IsNullOrEmpty(sessionID)) {

					return null;
				}

				string sessionName;

				switch (session) {
					case ESession.CamelCase:
						sessionName = "sessionID";

						break;
					case ESession.Lowercase:
						sessionName = "sessionid";

						break;
					case ESession.PascalCase:
						sessionName = "SessionID";

						break;
					default:

						return null;
				}

				if (data != null) {
					data[sessionName] = sessionID;
				} else {
					data = new Dictionary<string, string>(1, StringComparer.Ordinal) { { sessionName, sessionID } };
				}
			}

			WebBrowser.HtmlDocumentResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlPostToHtmlDocument(host + request, data, referer).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlPostToHtmlDocumentWithSession(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlPostToHtmlDocumentWithSession(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<T> UrlPostToJsonObjectWithSession<T>(string host, string request, Dictionary<string, string> data = null, string referer = null, ESession session = ESession.Lowercase, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) where T : class {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request) || !Enum.IsDefined(typeof(ESession), session)) {

				return null;
			}

			if (maxTries == 0) {

				return null;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			if (session != ESession.None) {
				string sessionID = WebBrowser.CookieContainer.GetCookieValue(host, "sessionid");

				if (string.IsNullOrEmpty(sessionID)) {

					return null;
				}

				string sessionName;

				switch (session) {
					case ESession.CamelCase:
						sessionName = "sessionID";

						break;
					case ESession.Lowercase:
						sessionName = "sessionid";

						break;
					case ESession.PascalCase:
						sessionName = "SessionID";

						break;
					default:

						return null;
				}

				if (data != null) {
					data[sessionName] = sessionID;
				} else {
					data = new Dictionary<string, string>(1, StringComparer.Ordinal) { { sessionName, sessionID } };
				}
			}

			WebBrowser.ObjectResponse<T> response = await WebLimitRequest(host, async () => await WebBrowser.UrlPostToJsonObject<T, Dictionary<string, string>>(host + request, data, referer).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<T> UrlPostToJsonObjectWithSession<T>(string host, string request, List<KeyValuePair<string, string>> data = null, string referer = null, ESession session = ESession.Lowercase, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) where T : class {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request) || !Enum.IsDefined(typeof(ESession), session)) {

				return null;
			}

			if (maxTries == 0) {

				return null;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, true, --maxTries).ConfigureAwait(false);
					}

					return null;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			if (session != ESession.None) {
				string sessionID = WebBrowser.CookieContainer.GetCookieValue(host, "sessionid");

				if (string.IsNullOrEmpty(sessionID)) {

					return null;
				}

				string sessionName;

				switch (session) {
					case ESession.CamelCase:
						sessionName = "sessionID";

						break;
					case ESession.Lowercase:
						sessionName = "sessionid";

						break;
					case ESession.PascalCase:
						sessionName = "SessionID";

						break;
					default:

						return null;
				}

				KeyValuePair<string, string> sessionValue = new KeyValuePair<string, string>(sessionName, sessionID);

				if (data != null) {
					data.Remove(sessionValue);
					data.Add(sessionValue);
				} else {
					data = new List<KeyValuePair<string, string>>(1) { sessionValue };
				}
			}

			WebBrowser.ObjectResponse<T> response = await WebLimitRequest(host, async () => await WebBrowser.UrlPostToJsonObject<T, List<KeyValuePair<string, string>>>(host + request, data, referer).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return null;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlPostToJsonObjectWithSession<T>(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return response.Content;
		}

		public async Task<bool> UrlPostWithSession(string host, string request, Dictionary<string, string> data = null, string referer = null, ESession session = ESession.Lowercase, bool checkSessionPreemptively = true, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(request) || !Enum.IsDefined(typeof(ESession), session)) {

				return false;
			}

			if (maxTries == 0) {

				return false;
			}

			if (checkSessionPreemptively) {
				// Check session preemptively as this request might not get redirected to expiration
				bool? sessionExpired = await IsSessionExpired().ConfigureAwait(false);

				if (sessionExpired.GetValueOrDefault(true)) {
					if (await RefreshSession().ConfigureAwait(false)) {
						return await UrlPostWithSession(host, request, data, referer, session, true, --maxTries).ConfigureAwait(false);
					}

					return false;
				}
			} else {
				// If session refresh is already in progress, just wait for it
				await SessionSemaphore.WaitAsync().ConfigureAwait(false);
				SessionSemaphore.Release();
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return false;
				}
			}

			if (session != ESession.None) {
				string sessionID = WebBrowser.CookieContainer.GetCookieValue(host, "sessionid");

				if (string.IsNullOrEmpty(sessionID)) {

					return false;
				}

				string sessionName;

				switch (session) {
					case ESession.CamelCase:
						sessionName = "sessionID";

						break;
					case ESession.Lowercase:
						sessionName = "sessionid";

						break;
					case ESession.PascalCase:
						sessionName = "SessionID";

						break;
					default:

						return false;
				}

				if (data != null) {
					data[sessionName] = sessionID;
				} else {
					data = new Dictionary<string, string>(1, StringComparer.Ordinal) { { sessionName, sessionID } };
				}
			}

			WebBrowser.BasicResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlPost(host + request, data, referer).ConfigureAwait(false)).ConfigureAwait(false);

			if (response == null) {
				return false;
			}

			if (IsSessionExpiredUri(response.FinalUri)) {
				if (await RefreshSession().ConfigureAwait(false)) {
					return await UrlPostWithSession(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
				}

				return false;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri).ConfigureAwait(false)) {

				return await UrlPostWithSession(host, request, data, referer, session, checkSessionPreemptively, --maxTries).ConfigureAwait(false);
			}

			return true;
		}

		public static async Task<T> WebLimitRequest<T>(string service, Func<Task<T>> function) {
			if (string.IsNullOrEmpty(service) || (function == null)) {

				return default;
			}

			if (MobileAuthenticator.WebLimiterDelay == 0) {
				return await function().ConfigureAwait(false);
			}

			if (!WebLimitingSemaphores.TryGetValue(service, out (SemaphoreSlim RateLimitingSemaphore, SemaphoreSlim OpenConnectionsSemaphore) limiters)) {
				if (!WebLimitingSemaphores.TryGetValue(nameof(WebHandler), out limiters)) {
					return await function().ConfigureAwait(false);
				}
			}

			// Sending a request opens a new connection
			await limiters.OpenConnectionsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				// It also increases number of requests
				await limiters.RateLimitingSemaphore.WaitAsync().ConfigureAwait(false);

				// We release rate-limiter semaphore regardless of our task completion, since we use that one only to guarantee rate-limiting of their creation
				Utilities.InBackground(
					async () => {
						await Task.Delay(MobileAuthenticator.WebLimiterDelay).ConfigureAwait(false);
						limiters.RateLimitingSemaphore.Release();
					}
				);

				return await function().ConfigureAwait(false);
			} finally {
				// We release open connections semaphore only once we're indeed done sending a particular request
				limiters.OpenConnectionsSemaphore.Release();
			}
		}

		internal HttpClient GenerateDisposableHttpClient() => WebBrowser.GenerateDisposableHttpClient();

		internal async Task<IDocument> GetConfirmations(string deviceID, string confirmationHash, uint time) {
			if (string.IsNullOrEmpty(deviceID) || string.IsNullOrEmpty(confirmationHash) || (time == 0)) {

				return null;
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			string request = "/mobileconf/conf?a=" + SteamID + "&k=" + WebUtility.UrlEncode(confirmationHash) + "&l=english&m=android&p=" + WebUtility.UrlEncode(deviceID) + "&t=" + time + "&tag=conf";

			return await UrlGetToHtmlDocumentWithSession(SteamCommunityURL, request).ConfigureAwait(false);
		}


		internal async Task<uint> GetServerTime() {
			KeyValue response = null;

			for (byte i = 0; (i < WebBrowser.MaxTries) && (response == null); i++) {
				using WebAPI.AsyncInterface iTwoFactorService = Bot.SteamConfiguration.GetAsyncWebAPIInterface(ITwoFactorService);

				iTwoFactorService.Timeout = WebBrowser.Timeout;

				try {
					response = await WebLimitRequest(
						WebAPI.DefaultBaseAddress.Host,

						// ReSharper disable once AccessToDisposedClosure
						async () => await iTwoFactorService.CallAsync(HttpMethod.Post, "QueryTime").ConfigureAwait(false)
					).ConfigureAwait(false);
				} catch (TaskCanceledException) {

				} catch (Exception) {

				}
			}

			if (response == null) {

				return 0;
			}

			uint result = response["server_time"].AsUnsignedInteger();

			if (result == 0) {

				return 0;
			}

			return result;
		}

		internal async Task<bool?> HandleConfirmation(string deviceID, string confirmationHash, uint time, ulong confirmationID, ulong confirmationKey, bool accept) {
			if (string.IsNullOrEmpty(deviceID) || string.IsNullOrEmpty(confirmationHash) || (time == 0) || (confirmationID == 0) || (confirmationKey == 0)) {

				return null;
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			string request = "/mobileconf/ajaxop?a=" + SteamID + "&cid=" + confirmationID + "&ck=" + confirmationKey + "&k=" + WebUtility.UrlEncode(confirmationHash) + "&l=english&m=android&op=" + (accept ? "allow" : "cancel") + "&p=" + WebUtility.UrlEncode(deviceID) + "&t=" + time + "&tag=conf";

			Steam.BooleanResponse response = await UrlGetToJsonObjectWithSession<Steam.BooleanResponse>(SteamCommunityURL, request).ConfigureAwait(false);

			return response?.Success;
		}

		internal async Task<bool?> HandleConfirmations(string deviceID, string confirmationHash, uint time, IReadOnlyCollection<MobileAuthenticator.Confirmation> confirmations, bool accept) {
			if (string.IsNullOrEmpty(deviceID) || string.IsNullOrEmpty(confirmationHash) || (time == 0) || (confirmations == null) || (confirmations.Count == 0)) {

				return null;
			}

			if (!Initialized) {
				for (byte i = 0; (i < MobileAuthenticator.ConnectionTimeout) && !Initialized /*&& Bot.IsConnectedAndLoggedOn*/; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Initialized) {

					return null;
				}
			}

			const string request = "/mobileconf/multiajaxop";

			// Extra entry for sessionID
			List<KeyValuePair<string, string>> data = new List<KeyValuePair<string, string>>(8 + (confirmations.Count * 2)) {
				new KeyValuePair<string, string>("a", SteamID.ToString()),
				new KeyValuePair<string, string>("k", confirmationHash),
				new KeyValuePair<string, string>("m", "android"),
				new KeyValuePair<string, string>("op", accept ? "allow" : "cancel"),
				new KeyValuePair<string, string>("p", deviceID),
				new KeyValuePair<string, string>("t", time.ToString()),
				new KeyValuePair<string, string>("tag", "conf")
			};

			foreach (MobileAuthenticator.Confirmation confirmation in confirmations) {
				data.Add(new KeyValuePair<string, string>("cid[]", confirmation.ID.ToString()));
				data.Add(new KeyValuePair<string, string>("ck[]", confirmation.Key.ToString()));
			}

			Steam.BooleanResponse response = await UrlPostToJsonObjectWithSession<Steam.BooleanResponse>(SteamCommunityURL, request, data).ConfigureAwait(false);

			return response?.Success;
		}

		internal async Task<bool> Init(ulong steamID, EUniverse universe, string webAPIUserNonce, string parentalCode = null) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || (universe == EUniverse.Invalid) || !Enum.IsDefined(typeof(EUniverse), universe) || string.IsNullOrEmpty(webAPIUserNonce)) {

				return false;
			}

			byte[] publicKey = KeyDictionary.GetPublicKey(universe);

			if ((publicKey == null) || (publicKey.Length == 0)) {

				return false;
			}

			// Generate a random 32-byte session key
			byte[] sessionKey = CryptoHelper.GenerateRandomBlock(32);

			// RSA encrypt our session key with the public key for the universe we're on
			byte[] encryptedSessionKey;

			using (RSACrypto rsa = new RSACrypto(publicKey)) {
				encryptedSessionKey = rsa.Encrypt(sessionKey);
			}

			// Generate login key from the user nonce that we've received from Steam network
			byte[] loginKey = Encoding.UTF8.GetBytes(webAPIUserNonce);

			// AES encrypt our login key with our session key
			byte[] encryptedLoginKey = CryptoHelper.SymmetricEncrypt(loginKey, sessionKey);

			// We're now ready to send the data to Steam API

			KeyValue response;

			// We do not use usual retry pattern here as webAPIUserNonce is valid only for a single request
			// Even during timeout, webAPIUserNonce is most likely already invalid
			// Instead, the caller is supposed to ask for new webAPIUserNonce and call Init() again on failure
			using (WebAPI.AsyncInterface iSteamUserAuth = Bot.SteamConfiguration.GetAsyncWebAPIInterface(ISteamUserAuth)) {
				iSteamUserAuth.Timeout = WebBrowser.Timeout;

				try {
					response = await WebLimitRequest(
						WebAPI.DefaultBaseAddress.Host,

						// ReSharper disable once AccessToDisposedClosure
						async () => await iSteamUserAuth.CallAsync(
							HttpMethod.Post, "AuthenticateUser", args: new Dictionary<string, object>(3, StringComparer.Ordinal) {
								{ "encrypted_loginkey", encryptedLoginKey },
								{ "sessionkey", encryptedSessionKey },
								{ "steamid", steamID }
							}
						).ConfigureAwait(false)
					).ConfigureAwait(false);
				} catch (TaskCanceledException) {

					return false;
				} catch (Exception) {

					return false;
				}
			}

			if (response == null) {
				return false;
			}

			string steamLogin = response["token"].AsString();

			if (string.IsNullOrEmpty(steamLogin)) {

				return false;
			}

			string steamLoginSecure = response["tokensecure"].AsString();

			if (string.IsNullOrEmpty(steamLoginSecure)) {

				return false;
			}

			string sessionID = Convert.ToBase64String(Encoding.UTF8.GetBytes(steamID.ToString()));

			WebBrowser.CookieContainer.Add(new Cookie("sessionid", sessionID, "/", "." + SteamCommunityHost));
			WebBrowser.CookieContainer.Add(new Cookie("sessionid", sessionID, "/", "." + SteamHelpHost));
			WebBrowser.CookieContainer.Add(new Cookie("sessionid", sessionID, "/", "." + SteamStoreHost));

			WebBrowser.CookieContainer.Add(new Cookie("steamLogin", steamLogin, "/", "." + SteamCommunityHost));
			WebBrowser.CookieContainer.Add(new Cookie("steamLogin", steamLogin, "/", "." + SteamHelpHost));
			WebBrowser.CookieContainer.Add(new Cookie("steamLogin", steamLogin, "/", "." + SteamStoreHost));

			WebBrowser.CookieContainer.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", "." + SteamCommunityHost));
			WebBrowser.CookieContainer.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", "." + SteamHelpHost));
			WebBrowser.CookieContainer.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", "." + SteamStoreHost));

			// Report proper time when doing timezone-based calculations, see setTimezoneCookies() from https://steamcommunity-a.akamaihd.net/public/shared/javascript/shared_global.js
			string timeZoneOffset = DateTimeOffset.Now.Offset.TotalSeconds + WebUtility.UrlEncode(",") + "0";

			WebBrowser.CookieContainer.Add(new Cookie("timezoneOffset", timeZoneOffset, "/", "." + SteamCommunityHost));
			WebBrowser.CookieContainer.Add(new Cookie("timezoneOffset", timeZoneOffset, "/", "." + SteamHelpHost));
			WebBrowser.CookieContainer.Add(new Cookie("timezoneOffset", timeZoneOffset, "/", "." + SteamStoreHost));

			// Unlock Steam Parental if needed
			if ((parentalCode != null) && (parentalCode.Length == 4)) {
				if (!await UnlockParentalAccount(parentalCode).ConfigureAwait(false)) {
					return false;
				}
			}

			LastSessionCheck = LastSessionRefresh = DateTime.UtcNow;
			Initialized = true;

			return true;
		}

		internal void OnDisconnected() {
			Initialized = false;
			CachedApiKey = null;
		}

		internal void OnVanityURLChanged(string vanityURL = null) => VanityURL = !string.IsNullOrEmpty(vanityURL) ? vanityURL : null;

		private async Task<(ESteamApiKeyState State, string Key)> GetApiKeyState() {
			const string request = "/dev/apikey?l=english";
			using IDocument htmlDocument = await UrlGetToHtmlDocumentWithSession(SteamCommunityURL, request).ConfigureAwait(false);

			IElement titleNode = htmlDocument?.SelectSingleNode("//div[@id='mainContents']/h2");

			if (titleNode == null) {
				return (ESteamApiKeyState.Timeout, null);
			}

			string title = titleNode.TextContent;

			if (string.IsNullOrEmpty(title)) {

				return (ESteamApiKeyState.Error, null);
			}

			if (title.Contains("Access Denied") || title.Contains("Validated email address required")) {
				return (ESteamApiKeyState.AccessDenied, null);
			}

			IElement htmlNode = htmlDocument.SelectSingleNode("//div[@id='bodyContents_ex']/p");

			if (htmlNode == null) {

				return (ESteamApiKeyState.Error, null);
			}

			string text = htmlNode.TextContent;

			if (string.IsNullOrEmpty(text)) {

				return (ESteamApiKeyState.Error, null);
			}

			if (text.Contains("Registering for a Steam Web API Key")) {
				return (ESteamApiKeyState.NotRegisteredYet, null);
			}

			int keyIndex = text.IndexOf("Key: ", StringComparison.Ordinal);

			if (keyIndex < 0) {

				return (ESteamApiKeyState.Error, null);
			}

			keyIndex += 5;

			if (text.Length <= keyIndex) {

				return (ESteamApiKeyState.Error, null);
			}

			text = text.Substring(keyIndex);

			if ((text.Length != 32) || !Utilities.IsValidHexadecimalText(text)) {

				return (ESteamApiKeyState.Error, null);
			}

			return (ESteamApiKeyState.Registered, text);
		}

		private async Task<bool> IsProfileUri(Uri uri, bool waitForInitialization = true) {
			if (uri == null) {

				return false;
			}

			string profileURL = await GetAbsoluteProfileURL(waitForInitialization).ConfigureAwait(false);

			if (string.IsNullOrEmpty(profileURL)) {

				return false;
			}

			return uri.AbsolutePath.Equals(profileURL);
		}

		private async Task<bool?> IsSessionExpired() {
			DateTime triggeredAt = DateTime.UtcNow;

			if (triggeredAt <= LastSessionCheck) {
				return LastSessionCheck != LastSessionRefresh;
			}

			await SessionSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (triggeredAt <= LastSessionCheck) {
					return LastSessionCheck != LastSessionRefresh;
				}

				// Choosing proper URL to check against is actually much harder than it initially looks like, we must abide by several rules to make this function as lightweight and reliable as possible
				// We should prefer to use Steam store, as the community is much more unstable and broken, plus majority of our requests get there anyway, so load-balancing with store makes much more sense. It also has a higher priority than the community, so all eventual issues should be fixed there first
				// The URL must be fast enough to render, as this function will be called reasonably often, and every extra delay adds up. We're already making our best effort by using HEAD request, but the URL itself plays a very important role as well
				// The page should have as little internal dependencies as possible, since every extra chunk increases likelihood of broken functionality. We can only make a guess here based on the amount of content that the page returns to us
				// It should also be URL with fairly fixed address that isn't going to disappear anytime soon, preferably something staple that is a dependency of other requests, so it's very unlikely to change in a way that would add overhead in the future
				// Lastly, it should be a request that is preferably generic enough as a routine check, not something specialized and targetted, to make it very clear that we're just checking if session is up, and to further aid internal dependencies specified above by rendering as general Steam info as possible

				const string host = SteamStoreURL;
				const string request = "/account";

				WebBrowser.BasicResponse response = await WebLimitRequest(host, async () => await WebBrowser.UrlHead(host + request).ConfigureAwait(false)).ConfigureAwait(false);

				if (response?.FinalUri == null) {
					return null;
				}

				bool result = IsSessionExpiredUri(response.FinalUri);

				DateTime now = DateTime.UtcNow;

				if (result) {
					Initialized = false;
				} else {
					LastSessionRefresh = now;
				}

				LastSessionCheck = now;

				return result;
			} finally {
				SessionSemaphore.Release();
			}
		}

		private static bool IsSessionExpiredUri(Uri uri) {
			if (uri == null) {

				return false;
			}

			return uri.AbsolutePath.StartsWith("/login", StringComparison.Ordinal) || uri.Host.Equals("lostauth");
		}

		private async Task<bool> RefreshSession() {
			/*if (!Bot.IsConnectedAndLoggedOn) {
				return false;
			}*/

			DateTime triggeredAt = DateTime.UtcNow;

			if (triggeredAt <= LastSessionCheck) {
				return LastSessionCheck == LastSessionRefresh;
			}

			await SessionSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (triggeredAt <= LastSessionCheck) {
					return LastSessionCheck == LastSessionRefresh;
				}

				Initialized = false;

				/*if (!Bot.IsConnectedAndLoggedOn) {
					return false;
				}
				*/

				bool result = await Bot.RefreshSession().ConfigureAwait(false);

				DateTime now = DateTime.UtcNow;

				if (result) {
					LastSessionRefresh = now;
				}

				LastSessionCheck = now;

				return result;
			} finally {
				SessionSemaphore.Release();
			}
		}

		private async Task<bool> RegisterApiKey() {
			const string request = "/dev/registerkey";

			// Extra entry for sessionID
			Dictionary<string, string> data = new Dictionary<string, string>(4, StringComparer.Ordinal) {
				{ "agreeToTerms", "agreed" },
				{ "domain", "generated.by." + nameof(SteamAuthNet).ToLowerInvariant() + ".localhost" },
				{ "Submit", "Register" }
			};

			return await UrlPostWithSession(SteamCommunityURL, request, data).ConfigureAwait(false);
		}

		private async Task<(bool Success, string Result)> ResolveApiKey() {
			if (Bot.IsAccountLimited) {
				// API key is permanently unavailable for limited accounts
				return (true, null);
			}

			(ESteamApiKeyState State, string Key) result = await GetApiKeyState().ConfigureAwait(false);

			switch (result.State) {
				case ESteamApiKeyState.AccessDenied:
					// We succeeded in fetching API key, but it resulted in access denied
					// Return empty result, API key is unavailable permanently
					return (true, "");
				case ESteamApiKeyState.NotRegisteredYet:
					// We succeeded in fetching API key, and it resulted in no key registered yet
					// Let's try to register a new key
					if (!await RegisterApiKey().ConfigureAwait(false)) {
						// Request timed out, bad luck, we'll try again later
						goto case ESteamApiKeyState.Timeout;
					}

					// We should have the key ready, so let's fetch it again
					result = await GetApiKeyState().ConfigureAwait(false);

					if (result.State == ESteamApiKeyState.Timeout) {
						// Request timed out, bad luck, we'll try again later
						goto case ESteamApiKeyState.Timeout;
					}

					if (result.State != ESteamApiKeyState.Registered) {
						// Something went wrong, report error
						goto default;
					}

					goto case ESteamApiKeyState.Registered;
				case ESteamApiKeyState.Registered:
					// We succeeded in fetching API key, and it resulted in registered key
					// Cache the result, this is the API key we want
					return (true, result.Key);
				case ESteamApiKeyState.Timeout:
					// Request timed out, bad luck, we'll try again later
					return (false, null);
				default:
					// We got an unhandled error, this should never happen

					return (false, null);
			}
		}

		private async Task<bool> UnlockParentalAccount(string parentalCode) {
			if (string.IsNullOrEmpty(parentalCode)) {

				return false;
			}

			bool[] results = await Task.WhenAll(UnlockParentalAccountForService(SteamCommunityURL, parentalCode), UnlockParentalAccountForService(SteamStoreURL, parentalCode)).ConfigureAwait(false);

			if (results.Any(result => !result)) {

				return false;
			}

			return true;
		}

		private async Task<bool> UnlockParentalAccountForService(string serviceURL, string parentalCode, byte maxTries = WebBrowser.MaxTries) {
			if (string.IsNullOrEmpty(serviceURL) || string.IsNullOrEmpty(parentalCode)) {

				return false;
			}

			const string request = "/parental/ajaxunlock";

			if (maxTries == 0) {

				return false;
			}

			string sessionID = WebBrowser.CookieContainer.GetCookieValue(serviceURL, "sessionid");

			if (string.IsNullOrEmpty(sessionID)) {

				return false;
			}

			Dictionary<string, string> data = new Dictionary<string, string>(2, StringComparer.Ordinal) {
				{ "pin", parentalCode },
				{ "sessionid", sessionID }
			};

			// This request doesn't go through UrlPostRetryWithSession as we have no access to session refresh capability (this is in fact session initialization)
			WebBrowser.BasicResponse response = await WebLimitRequest(serviceURL, async () => await WebBrowser.UrlPost(serviceURL + request, data, serviceURL).ConfigureAwait(false)).ConfigureAwait(false);

			if ((response == null) || IsSessionExpiredUri(response.FinalUri)) {
				// There is no session refresh capability at this stage
				return false;
			}

			// Under special brain-damaged circumstances, Steam might just return our own profile as a response to the request, for absolutely no reason whatsoever - just try again in this case
			if (await IsProfileUri(response.FinalUri, false).ConfigureAwait(false)) {

				return await UnlockParentalAccountForService(serviceURL, parentalCode, --maxTries).ConfigureAwait(false);
			}

			return true;
		}

		public enum ESession : byte {
			None,
			Lowercase,
			CamelCase,
			PascalCase
		}

		private enum ESteamApiKeyState : byte {
			Error,
			Timeout,
			Registered,
			NotRegisteredYet,
			AccessDenied
		}
	}
}
