using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnkrSDK.WalletConnectSharp.Core.Events;
using AnkrSDK.WalletConnectSharp.Core.Events.Model;
using AnkrSDK.WalletConnectSharp.Core.Models;
using AnkrSDK.WalletConnectSharp.Core.Models.Ethereum;
using AnkrSDK.WalletConnectSharp.Core.Models.Ethereum.Types;
using AnkrSDK.WalletConnectSharp.Core.Network;
using AnkrSDK.WalletConnectSharp.Core.Utils;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AnkrSDK.WalletConnectSharp.Core
{
	public class WalletConnectSession : WalletConnectProtocol, IWalletConnectCommunicator
	{
		public event Action OnSessionConnect;
		public event Action OnSessionCreated;
		public event Action OnSessionResumed;
		public event Action OnSessionDisconnect;
		public event Action OnSend;
		public event Action<WCSessionData> SessionUpdate;
		public event Action<string[]> OnAccountChanged;
		public event Action<int> OnChainChanged;
		public event Action OnSessionRequestSent;
		public int NetworkId { get; private set; }
		public string[] Accounts { get; private set; }
		public int ChainId { get; private set; }

		private readonly string _clientId = "";

		private readonly string _handshakeTopic;

		private long _handshakeId;

		private UniTaskCompletionSource<WCSessionData> _sessionCreationCompletionSource;


		public string URI
		{
			get
			{
				var topicEncode = WebUtility.UrlEncode(_handshakeTopic);
				var versionEncode = WebUtility.UrlEncode(Version);
				var bridgeUrlEncode = WebUtility.UrlEncode(BridgeUrl);
				var keyEncoded = WebUtility.UrlEncode(Key);

				return "wc:" + topicEncode + "@" + versionEncode + "?bridge=" + bridgeUrlEncode + "&key=" + keyEncoded;
			}
		}

		public WalletConnectSession(SavedSession savedSession, ITransport transport = null, ICipher cipher = null,
			EventDelegator eventDelegator = null) : base(savedSession, transport, cipher, eventDelegator)
		{
			DappMetadata = savedSession.DappMeta;
			WalletMetadata = savedSession.WalletMeta;
			ChainId = savedSession.ChainID;

			_clientId = savedSession.ClientID;

			Accounts = savedSession.Accounts;

			NetworkId = savedSession.NetworkID;

			_handshakeId = savedSession.HandshakeID;
			SubscribeForSessionResponse();
			WalletConnected = true;
		}

		public WalletConnectSession(ClientMeta clientMeta, string bridgeUrl = null, ITransport transport = null,
			ICipher cipher = null, int chainId = 1, EventDelegator eventDelegator = null) : base(transport, cipher,
			eventDelegator)
		{
			if (clientMeta == null)
			{
				throw new ArgumentException("clientMeta cannot be null!");
			}

			if (string.IsNullOrWhiteSpace(clientMeta.Description))
			{
				throw new ArgumentException("clientMeta must include a valid Description");
			}

			if (string.IsNullOrWhiteSpace(clientMeta.Name))
			{
				throw new ArgumentException("clientMeta must include a valid Name");
			}

			if (string.IsNullOrWhiteSpace(clientMeta.Url))
			{
				throw new ArgumentException("clientMeta must include a valid URL");
			}

			if (clientMeta.Icons == null || clientMeta.Icons.Length == 0)
			{
				throw new ArgumentException(
					"clientMeta must include an array of Icons the Wallet app can use. These Icons must be URLs to images. You must include at least one image URL to use");
			}

			if (bridgeUrl == null)
			{
				bridgeUrl = DefaultBridge.ChooseRandomBridge();
			}

			bridgeUrl = DefaultBridge.GetBridgeUrl(bridgeUrl);

			bridgeUrl = WSUrlFormatter.GetReadyToUseURL(bridgeUrl);

			DappMetadata = clientMeta;
			ChainId = chainId;
			BridgeUrl = bridgeUrl;

			BridgeUrl = DefaultBridge.GetBridgeUrl(BridgeUrl);

			var topicGuid = Guid.NewGuid();

			_handshakeTopic = topicGuid.ToString();

			_clientId = Guid.NewGuid().ToString();

			GenerateKey();

			Transport?.ClearSubscriptions();
			WalletConnected = false;
		}

		private void GenerateKey()
		{
			//Generate a random secret
			var secret = new byte[32];
			var rngCsp = new RNGCryptoServiceProvider();
			rngCsp.GetBytes(secret);

			KeyRaw = secret;

			//Convert hex 
			Key = KeyRaw.ToHex().ToLower();
		}

		public async UniTask<WCSessionData> ConnectSession()
		{
			var prevStatus = Status;
			try
			{
				if (!TransportConnected)
				{
					await OpenTransport();
				}
				else
				{
					Debug.Log("Transport already connected. No need to setup");
				}

				await SubscribeAndListenToTopic(_clientId);

				ListenToTopic(_handshakeTopic);

				WCSessionData result;
				if (prevStatus == WalletConnectStatus.DisconnectedNoSession)
				{
					result = await CreateSession();
					Connecting = false;
					OnSessionCreated?.Invoke();
				}
				else
				{
					result = new WCSessionData
					{
						accounts = Accounts, approved = true, chainId = ChainId, networkId = NetworkId,
						peerId = PeerId, peerMeta = WalletMetadata
					};
					Connecting = false;

					OnSessionResumed?.Invoke();
				}

				Debug.Log($"Chain ID == {ChainId}");
				OnSessionConnect?.Invoke();

				return result;
			}
			catch (IOException e)
			{
				//If the transport is connected, then disconnect that
				//we tried our best, they can try again
				if (TransportConnected)
				{
					await DisconnectTransport();
				}
				else
				{
					throw new IOException("Transport Connection failed", e);
				}

				throw new IOException("Session Connection failed", e);
			}
			finally
			{
				//The session has been made, we are no longer ready for another user prompt
				Connecting = false;
			}
		}

		public async UniTask DisconnectSession()
		{
			var request = new WCSessionUpdate(new WCSessionData
			{
				approved = false, chainId = 0, accounts = null, networkId = 0
			});

			await SendRequest(request);

			await DisconnectTransport();

			HandleSessionDisconnect();
		}

		public virtual async UniTask<string> WalletAddEthChain(EthChainData chainData)
		{
			var request = new WalletAddEthChain(chainData);
			var response = await Send<WalletAddEthChain, EthResponse>(request);
			return response.Result;
		}

		public virtual async UniTask<string> WalletSwitchEthChain(EthChain chainData)
		{
			var request = new WalletSwitchEthChain(chainData);
			var response = await Send<WalletSwitchEthChain, EthResponse>(request);
			return response.Result;
		}

		public virtual async UniTask<string> WalletUpdateEthChain(EthUpdateChainData chainData)
		{
			var request = new WalletUpdateEthChain(chainData);
			var response = await Send<WalletUpdateEthChain, EthResponse>(request);
			return response.Result;
		}

		public async UniTask<string> EthSign(string address, string message)
		{
			if (!message.IsHex())
			{
				var rawMessage = Encoding.UTF8.GetBytes(message);

				var byteList = new List<byte>();
				var bytePrefix = "0x19".HexToByteArray();
				var textBytePrefix = Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + rawMessage.Length);

				byteList.AddRange(bytePrefix);
				byteList.AddRange(textBytePrefix);
				byteList.AddRange(rawMessage);

				var hash = new Sha3Keccack().CalculateHash(byteList.ToArray());

				message = "0x" + hash.ToHex();
			}

			Debug.Log(message);

			var request = new EthSign(address, message);

			var response = await Send<EthSign, EthResponse>(request);

			return response.Result;
		}

		public async UniTask<string> EthPersonalSign(string address, string message)
		{
			if (!message.IsHex())
			{
				/*var rawMessage = Encoding.UTF8.GetBytes(message);
				
				var byteList = new List<byte>();
				var bytePrefix = "0x19".HexToByteArray();
				var textBytePrefix = Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + rawMessage.Length);

				byteList.AddRange(bytePrefix);
				byteList.AddRange(textBytePrefix);
				byteList.AddRange(rawMessage);
				
				var hash = new Sha3Keccack().CalculateHash(byteList.ToArray());*/

				message = "0x" + Encoding.UTF8.GetBytes(message).ToHex();
			}

			var request = new EthPersonalSign(address, message);

			var response = await Send<EthPersonalSign, EthResponse>(request);


			return response.Result;
		}

		public async UniTask<string> EthSignTypedData<T>(string address, T data, EIP712Domain eip712Domain)
		{
			var request = new EthSignTypedData<T>(address, data, eip712Domain);

			var response = await Send<EthSignTypedData<T>, EthResponse>(request);

			return response.Result;
		}

		public async UniTask<string> EthSendTransaction(params TransactionData[] transaction)
		{
			var request = new EthSendTransaction(transaction);
			var response = await Send<EthSendTransaction, EthResponse>(request);
			return response.Result;
		}

		public async UniTask<string> EthSignTransaction(params TransactionData[] transaction)
		{
			var request = new EthSignTransaction(transaction);

			var response = await Send<EthSignTransaction, EthResponse>(request);

			return response.Result;
		}

		public async UniTask<string> EthSendRawTransaction(string data, Encoding messageEncoding = null)
		{
			if (!data.IsHex())
			{
				var encoding = messageEncoding;
				if (encoding == null)
				{
					encoding = Encoding.UTF8;
				}

				data = "0x" + encoding.GetBytes(data).ToHex();
			}

			var request = new EthGenericRequest<string>("eth_sendRawTransaction", data);

			var response = await Send<EthGenericRequest<string>, EthResponse>(request);

			return response.Result;
		}

		public async UniTask<TResponse> Send<TRequest, TResponse>(TRequest data)
			where TRequest : JsonRpcRequest
			where TResponse : JsonRpcResponse
		{
			var eventCompleted = new UniTaskCompletionSource<TResponse>();

			void HandleSendResponse(object sender, JsonRpcResponseEvent<TResponse> @event)
			{
				var response = @event.Response;
				if (response.IsError)
				{
					eventCompleted.TrySetException(new IOException(response.Error.Message));
				}
				else
				{
					eventCompleted.TrySetResult(@event.Response);
				}
			}

			EventDelegator.ListenForResponse<TResponse>(data.ID, HandleSendResponse);

			await SendRequest(data);

			OnSend?.Invoke();

			return await eventCompleted.Task;
		}

		/// <summary>
		///     Create a new WalletConnect session with a Wallet.
		/// </summary>
		/// <returns></returns>
		private async UniTask<WCSessionData> CreateSession()
		{
			var data = new WcSessionRequest(DappMetadata, _clientId, ChainId);

			_handshakeId = data.ID;
			SubscribeForSessionResponse();

			//sending session request
			await SendRequest(data, _handshakeTopic);

			if (_sessionCreationCompletionSource != null)
			{
				throw new InvalidOperationException("Two session can not be created at the same time");
			}

			_sessionCreationCompletionSource = new UniTaskCompletionSource<WCSessionData>();

			WaitingForSessionRequestResponse = true;
			OnSessionRequestSent?.Invoke();

			Debug.Log("[WalletConnect] Session Ready for Wallet");

			var response = await _sessionCreationCompletionSource.Task;

			WaitingForSessionRequestResponse = false;

			return response;
		}

		private void HandleConnectMessage(WCSessionData result)
		{
			if (_sessionCreationCompletionSource != null)
			{
				_sessionCreationCompletionSource.TrySetResult(result);
				_sessionCreationCompletionSource = null;
			}
		}

		private void HandleConnectionFailureMessage(string peerId, string message)
		{
			if (_sessionCreationCompletionSource != null)
			{
				if (message == "Not Approved" || message == "Session Rejected")
				{
					_sessionCreationCompletionSource.TrySetCanceled();
				}
				else
				{
					_sessionCreationCompletionSource.TrySetException(
						new IOException("WalletConnect: Session Failed: " + message));
				}

				Debug.LogError("Session failed with message: " + message);

				_sessionCreationCompletionSource = null;
			}
		}

		private void SubscribeForSessionResponse()
		{
			void HandleSessionRequestResponse(object sender, JsonRpcResponseEvent<WCSessionRequestResponse> jsonResponse)
			{
				var response = jsonResponse.Response.result;

				if (response?.approved == true)
				{
					HandleSessionUpdate(response);
				}
				else
				{
					var msg = jsonResponse.Response.IsError ? jsonResponse.Response.Error.Message : "Not Approved";
					HandleConnectionFailureMessage(jsonResponse.Response.result.peerId, msg);
					HandleSessionDisconnect();
				}
			}

			EventDelegator.ListenForResponse<WCSessionRequestResponse>(_handshakeId, HandleSessionRequestResponse);

			void HandleSessionUpdateResponse(object _, GenericEvent<WCSessionUpdate> @event)
			{
				var wcSessionData = @event.Response.parameters[0];
				HandleSessionUpdate(wcSessionData);
			}

			EventDelegator.ListenForGeneric<WCSessionUpdate>(WCSessionUpdate.SessionUpdateMethod, HandleSessionUpdateResponse);
		}

		private void HandleSessionUpdate(WCSessionData data)
		{
			if (data == null)
			{
				return;
			}

			var wasConnected = WalletConnected;

			//We are connected if we are approved
			WalletConnected = data.approved;
			Debug.Log($"WalletConnectSession: SessionConnected set to {data.approved}");

			if (data.chainId != null)
			{
				var oldChainId = ChainId;
				ChainId = (int)data.chainId;

				if (oldChainId != ChainId)
				{
					OnChainChanged?.Invoke((int)data.chainId);
					Debug.Log("ChainID Changed, New ChainID: " + (int)data.chainId);
				}
			}

			if (data.networkId != null)
			{
				NetworkId = (int)data.networkId;
			}

			var dataAccount = data.accounts?[0];
			var oldAccount = Accounts?[0];

			Accounts = data.accounts;
			if (oldAccount != dataAccount)
			{
				OnAccountChanged?.Invoke(data.accounts);
				Debug.Log("Account Changed, currently connected account : " + dataAccount);
			}

			switch (wasConnected)
			{
				case false:
					PeerId = data.peerId;
					WalletMetadata = data.peerMeta;
					HandleConnectMessage(data);
					break;
				case true when !WalletConnected:
					HandleSessionDisconnect();
					break;
			}

			SessionUpdate?.Invoke(data);
		}

		private void HandleSessionDisconnect()
		{
			if (TransportConnected)
			{
				DisconnectTransport().AsTask().ConfigureAwait(false);
			}

			ActiveTopics.Clear();

			EventDelegator.Clear();

			WalletConnected = false;

			OnSessionDisconnect?.Invoke();
		}

		/// <summary>
		///     Creates and returns a serializable class that holds all session data required to resume later
		/// </summary>
		/// <returns></returns>
		public SavedSession GetSavedSession()
		{
			if (Status == WalletConnectStatus.DisconnectedNoSession)
			{
				return null;
			}

			return new SavedSession(_clientId, _handshakeId, BridgeUrl, Key, KeyRaw, PeerId, NetworkId, Accounts,
				ChainId, DappMetadata, WalletMetadata);
		}
	}
}