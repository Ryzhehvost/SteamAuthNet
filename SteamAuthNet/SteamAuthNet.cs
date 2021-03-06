﻿// Copyright 2020 Andrii "Ryzhehvost" Kotlyar
// Contact: ryzhehvost@kotei.co.ua
// This derivative work is based on MobileAuthenticator.cs and Steam.cs from https://github.com/JustArchiNET/ArchiSteamFarm/tree/0ce04415
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
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Newtonsoft.Json;

namespace SteamAuthNet {
	[SuppressMessage("ReSharper", "ClassCannotBeInstantiated")]
	public class MobileAuthenticator {

		[JsonIgnore]
		public static int ConnectionTimeout { get; set; } = 90;
		[JsonIgnore]
		public static int WebLimiterDelay {get; set; } = 300;
		[JsonIgnore]
		public static int ConfirmationsLimiterDelay { get; set; } = 10;

		public DateTime LastPacketReceived { get; private set; }

		internal WebHandler WebHandler;
		internal const byte CodeDigits = 5;

		private const byte CodeInterval = 30;
		private const byte SteamTimeTTL = 24; // For how many hours we can assume that SteamTimeDifference is correct

		private static readonly char[] CodeCharacters = { '2', '3', '4', '5', '6', '7', '8', '9', 'B', 'C', 'D', 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'P', 'Q', 'R', 'T', 'V', 'W', 'X', 'Y' };
		private static readonly SemaphoreSlim ConfirmationsSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim TimeSemaphore = new SemaphoreSlim(1, 1);

		private static DateTime LastSteamTimeCheck;
		private static int? SteamTimeDifference;

		internal bool HasValidDeviceID => !string.IsNullOrEmpty(DeviceID) && IsValidDeviceID(DeviceID);

#pragma warning disable 649
		[JsonProperty(PropertyName = "identity_secret", Required = Required.Always)]
		private readonly string IdentitySecret;
#pragma warning restore 649

#pragma warning disable 649
		[JsonProperty(PropertyName = "shared_secret", Required = Required.Always)]
		private readonly string SharedSecret;
#pragma warning restore 649

		[JsonProperty(PropertyName = "device_id")]
		private string DeviceID;

		[JsonConstructor]
		private MobileAuthenticator() { }

		internal void CorrectDeviceID(string deviceID) {
			if (string.IsNullOrEmpty(deviceID)) {
				return;
			}

			if (!IsValidDeviceID(deviceID)) {
				return;
			}

			DeviceID = deviceID;
		}

		internal async Task<string> GenerateToken() {
			uint time = await GetSteamTime().ConfigureAwait(false);

			if (time == 0) {
				return null;
			}

			return GenerateTokenForTime(time);
		}

		internal async Task<HashSet<Confirmation>> GetConfirmations() {
			if (!HasValidDeviceID) {
				return null;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);

			if (time == 0) {
				return null;
			}

			string confirmationHash = GenerateConfirmationHash(time, "conf");

			if (string.IsNullOrEmpty(confirmationHash)) {
				return null;
			}

			await LimitConfirmationsRequestsAsync().ConfigureAwait(false);

			using IDocument htmlDocument = await WebHandler.GetConfirmations(DeviceID, confirmationHash, time).ConfigureAwait(false);

			if (htmlDocument == null) {
				return null;
			}

			HashSet<Confirmation> result = new HashSet<Confirmation>();

			List<IElement> confirmationNodes = htmlDocument.SelectNodes("//div[@class='mobileconf_list_entry']");

			if (confirmationNodes.Count == 0) {
				return result;
			}

			foreach (IElement confirmationNode in confirmationNodes) {
				string idText = confirmationNode.GetAttributeValue("data-confid");

				if (string.IsNullOrEmpty(idText)) {

					return null;
				}

				if (!ulong.TryParse(idText, out ulong id) || (id == 0)) {

					return null;
				}

				string keyText = confirmationNode.GetAttributeValue("data-key");

				if (string.IsNullOrEmpty(keyText)) {

					return null;
				}

				if (!ulong.TryParse(keyText, out ulong key) || (key == 0)) {

					return null;
				}

				string creatorText = confirmationNode.GetAttributeValue("data-creator");

				if (string.IsNullOrEmpty(creatorText)) {
					return null;
				}

				if (!ulong.TryParse(creatorText, out ulong creator) || (creator == 0)) {
					return null;
				}

				string typeText = confirmationNode.GetAttributeValue("data-type");

				if (string.IsNullOrEmpty(typeText)) {

					return null;
				}

				if (!Enum.TryParse(typeText, out EType type) || (type == EType.Unknown)) {

					return null;
				}

				if (!Enum.IsDefined(typeof(EType), type)) {

					return null;
				}

/*				if (acceptedType.HasValue && (acceptedType.Value != type)) {
					continue;
				}
*/
				result.Add(new Confirmation(id, key, creator, type));
			}

			return result;
		}

		internal async Task<bool> HandleConfirmations(IReadOnlyCollection<Confirmation> confirmations, bool accept) {
			if ((confirmations == null) || (confirmations.Count == 0)) {

				return false;
			}

			if (!HasValidDeviceID) {

				return false;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);

			if (time == 0) {

				return false;
			}

			string confirmationHash = GenerateConfirmationHash(time, "conf");

			if (string.IsNullOrEmpty(confirmationHash)) {

				return false;
			}

			bool? result = await WebHandler.HandleConfirmations(DeviceID, confirmationHash, time, confirmations, accept).ConfigureAwait(false);

			if (!result.HasValue) {
				// Request timed out
				return false;
			}

			if (result.Value) {
				// Request succeeded
				return true;
			}

			// Our multi request failed, this is almost always Steam issue that happens randomly
			// In this case, we'll accept all pending confirmations one-by-one, synchronously (as Steam can't handle them in parallel)
			// We totally ignore actual result returned by those calls, abort only if request timed out
			foreach (Confirmation confirmation in confirmations) {
				bool? confirmationResult = await WebHandler.HandleConfirmation(DeviceID, confirmationHash, time, confirmation.ID, confirmation.Key, accept).ConfigureAwait(false);

				if (!confirmationResult.HasValue) {
					return false;
				}
			}

			return true;
		}

		internal static bool IsValidDeviceID(string deviceID) {
			if (string.IsNullOrEmpty(deviceID)) {

				return false;
			}

			// This one is optional
			int deviceIdentifierIndex = deviceID.IndexOf(':');

			if (deviceIdentifierIndex >= 0) {
				deviceIdentifierIndex++;

				if (deviceID.Length <= deviceIdentifierIndex) {
					return false;
				}

				deviceID = deviceID.Substring(deviceIdentifierIndex);
			}

			// Dashes are optional in the ID, strip them off for comparison
			string hash = deviceID.Replace("-", "");

			return (hash.Length > 0) && (Utilities.IsValidDigitsText(hash) || Utilities.IsValidHexadecimalText(hash));
		}

		private string GenerateConfirmationHash(uint time, string tag = null) {
			if (time == 0) {

				return null;
			}

			byte[] identitySecret;

			try {
				identitySecret = Convert.FromBase64String(IdentitySecret);
			} catch (FormatException) {
				return null;
			}

			byte bufferSize = 8;

			if (!string.IsNullOrEmpty(tag)) {
				bufferSize += (byte) Math.Min(32, tag.Length);
			}

			byte[] timeArray = BitConverter.GetBytes((long) time);

			if (BitConverter.IsLittleEndian) {
				Array.Reverse(timeArray);
			}

			byte[] buffer = new byte[bufferSize];

			Array.Copy(timeArray, buffer, 8);

			if (!string.IsNullOrEmpty(tag)) {
				Array.Copy(Encoding.UTF8.GetBytes(tag), 0, buffer, 8, bufferSize - 8);
			}

			using HMACSHA1 hmac = new HMACSHA1(identitySecret);

			byte[] hash = hmac.ComputeHash(buffer);

			return Convert.ToBase64String(hash);
		}

		private string GenerateTokenForTime(uint time) {
			if (time == 0) {

				return null;
			}

			byte[] sharedSecret;

			try {
				sharedSecret = Convert.FromBase64String(SharedSecret);
			} catch (FormatException) {

				return null;
			}

			byte[] timeArray = BitConverter.GetBytes((long) time / CodeInterval);

			if (BitConverter.IsLittleEndian) {
				Array.Reverse(timeArray);
			}

			byte[] hash;

			using (HMACSHA1 hmac = new HMACSHA1(sharedSecret)) {
				hash = hmac.ComputeHash(timeArray);
			}

			// The last 4 bits of the mac say where the code starts
			int start = hash[^1] & 0x0f;

			// Extract those 4 bytes
			byte[] bytes = new byte[4];

			Array.Copy(hash, start, bytes, 0, 4);

			if (BitConverter.IsLittleEndian) {
				Array.Reverse(bytes);
			}

			uint fullCode = BitConverter.ToUInt32(bytes, 0) & 0x7fffffff;

			// Build the alphanumeric code
			StringBuilder code = new StringBuilder(CodeDigits, CodeDigits);

			for (byte i = 0; i < CodeDigits; i++) {
				code.Append(CodeCharacters[fullCode % CodeCharacters.Length]);
				fullCode /= (uint) CodeCharacters.Length;
			}

			return code.ToString();
		}

		private async Task<uint> GetSteamTime() {
			if (SteamTimeDifference.HasValue && (DateTime.UtcNow.Subtract(LastSteamTimeCheck).TotalHours < SteamTimeTTL)) {
				return (uint) (Utilities.GetUnixTime() + SteamTimeDifference.Value);
			}

			await TimeSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (SteamTimeDifference.HasValue && (DateTime.UtcNow.Subtract(LastSteamTimeCheck).TotalHours < SteamTimeTTL)) {
					return (uint) (Utilities.GetUnixTime() + SteamTimeDifference.Value);
				}

				uint serverTime = await WebHandler.GetServerTime().ConfigureAwait(false);

				if (serverTime == 0) {
					return Utilities.GetUnixTime();
				}

				SteamTimeDifference = (int) (serverTime - Utilities.GetUnixTime());
				LastSteamTimeCheck = DateTime.UtcNow;

				return (uint) (Utilities.GetUnixTime() + SteamTimeDifference.Value);
			} finally {
				TimeSemaphore.Release();
			}
		}

		private static async Task LimitConfirmationsRequestsAsync() {
			if (ConfirmationsLimiterDelay == 0) {
				return;
			}

			await ConfirmationsSemaphore.WaitAsync().ConfigureAwait(false);

			Utilities.InBackground(
				async () => {
					await Task.Delay(ConfirmationsLimiterDelay * 1000).ConfigureAwait(false);
					ConfirmationsSemaphore.Release();
				}
			);
		}

		public sealed class Confirmation {
			internal readonly ulong Creator;
			internal readonly ulong ID;
			internal readonly ulong Key;
			internal readonly EType Type;

			internal Confirmation(ulong id, ulong key, ulong creator, EType type) {
				if ((id == 0) || (key == 0) || (creator == 0) || !Enum.IsDefined(typeof(EType), type)) {
					throw new ArgumentNullException(nameof(id) + " || " + nameof(key) + " || " + nameof(creator) + " || " + nameof(type));
				}

				ID = id;
				Key = key;
				Creator = creator;
				Type = type;
			}
		}
	}

	public enum EType : byte {
		Unknown,
		Generic,
		Trade,
		Market,

		// We're missing information about definition of number 4 type
		PhoneNumberChange = 5,
		AccountRecovery = 6
	}

	public class BooleanResponse {
		[JsonProperty(PropertyName = "success", Required = Required.Always)]
		public readonly bool Success;

		[JsonConstructor]
		protected BooleanResponse() { }
	}

	public sealed class ConfirmationDetails : BooleanResponse {
		internal MobileAuthenticator.Confirmation Confirmation { get; set; }
		internal ulong TradeOfferID { get; private set; }
		internal EType Type { get; private set; }

		[JsonProperty(PropertyName = "html", Required = Required.DisallowNull)]
		private string HTML {
			set {
				if (string.IsNullOrEmpty(value)) {
					return;
				}

				using IDocument htmlDocument = WebBrowser.StringToHtmlDocument(value).Result;

				if (htmlDocument == null) {
					return;
				}

				if (htmlDocument.SelectSingleNode("//div[@class='mobileconf_trade_area']") != null) {
					Type = EType.Trade;

					IElement tradeOfferNode = htmlDocument.SelectSingleNode("//div[@class='tradeoffer']");

					if (tradeOfferNode == null) {

						return;
					}

					string idText = tradeOfferNode.GetAttributeValue("id");

					if (string.IsNullOrEmpty(idText)) {

						return;
					}

					int index = idText.IndexOf('_');

					if (index < 0) {

						return;
					}

					index++;

					if (idText.Length <= index) {

						return;
					}

					idText = idText.Substring(index);

					if (!ulong.TryParse(idText, out ulong tradeOfferID) || (tradeOfferID == 0)) {

						return;
					}

					TradeOfferID = tradeOfferID;
				} else if (htmlDocument.SelectSingleNode("//div[@class='mobileconf_listing_prices']") != null) {
					Type = EType.Market;
				} else {
					// Normally this should be reported, but under some specific circumstances we might actually receive this one
					Type = EType.Generic;
				}
			}
		}
	}
}
