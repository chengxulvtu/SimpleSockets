using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleSockets.Helpers;
using SimpleSockets.Helpers.Compression;
using SimpleSockets.Helpers.Cryptography;
using SimpleSockets.Helpers.Serialization;
using SimpleSockets.Messaging;

namespace SimpleSockets.Server {

    public abstract class SimpleServer: SimpleSocket
    {

        #region Events
        
        /// <summary>
        /// Event invoked when a client connects to the server.
        /// </summary>
        public event EventHandler<ClientInfoEventArgs> ClientConnected;
        protected virtual void OnClientConnected(ClientInfoEventArgs eventArgs) => ClientConnected?.Invoke(this, eventArgs);

        /// <summary>
        /// Event invoked when a client disconnects from the server.
        /// </summary>
        public event EventHandler<ClientDisconnectedEventArgs> ClientDisconnected;
        protected virtual void OnClientDisconnected(ClientDisconnectedEventArgs eventArgs) => ClientDisconnected?.Invoke(this, eventArgs);

        /// <summary>
        /// Event invoked when a server starts listening for connections.
        /// </summary>
        public event EventHandler ServerStartedListening;
        protected virtual void OnServerStartedListening() => ServerStartedListening?.Invoke(this, null);

		/// <summary>
		/// Event fired when the server successfully validates the ssl certificate.
		/// </summary>
		public event EventHandler<ClientInfoEventArgs> SslAuthSuccess;
		protected virtual void OnSslAuthSuccess(ClientInfoEventArgs eventArgs) => SslAuthSuccess?.Invoke(this, eventArgs);

		/// <summary>
		/// Event fired when server is unable to validate the ssl certificate
		/// </summary>
		public event EventHandler<ClientInfoEventArgs> SslAuthFailed;
		protected virtual void OnSslAutFailed(ClientInfoEventArgs eventArgs) => SslAuthFailed?.Invoke(this, eventArgs);

		/// <summary>
		/// Event invoked when the server received a message from a client.
		/// </summary>
		public event EventHandler<ClientMessageReceivedEventArgs> MessageReceived;
		protected virtual void OnClientMessageReceived(ClientMessageReceivedEventArgs eventArgs) => MessageReceived?.Invoke(this, eventArgs);

		/// <summary>
		/// Event invoked when the server receives an object from a client.
		/// </summary>
		public event EventHandler<ClientObjectReceivedEventArgs> ObjectReceived;
		protected virtual void OnClientObjectReceived(ClientObjectReceivedEventArgs eventArgs) => ObjectReceived?.Invoke(this, eventArgs);

		/// <summary>
		/// Event invoked when the server receives bytes from a client.
		/// </summary>
		public event EventHandler<ClientBytesReceivedEventArgs> BytesReceived;
		protected virtual void OnClientBytesReceived(ClientBytesReceivedEventArgs eventArgs) => BytesReceived?.Invoke(this, eventArgs);

		#endregion

		#region Variables

		protected X509Certificate2 _serverCertificate = null;
		protected TlsProtocol _tlsProtocol;

		public bool AcceptInvalidCertificates { get; set; }
		public bool MutualAuthentication { get; set; }

		private Action<string> _logger;

        public override Action<string> Logger { 
            get => _logger;
            set {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                SocketLogger = LogHelper.InitializeLogger(false, SslEncryption , SocketProtocolType.Tcp == this.SocketProtocol, value, this.LoggerLevel);
                _logger = value;
            }
        }

        public int MaximumConnections { get; private set; } = 500;

        protected readonly ManualResetEventSlim CanAcceptConnections = new ManualResetEventSlim(false);

        protected Socket Listener { get; set; }

        public bool Listening { get; set; }

        public int ListenerPort { get; set; }

        public string ListenerIp { get; set; }

		private TimeSpan _timeout = new TimeSpan(0, 0, 0);

		/// <summary>
		/// Time until a client will timeout.
		/// Value cannot be less then 5 seconds
		/// Default value is 0 which means the server will wait indefinitely until it gets a response. 
		/// </summary>
		public TimeSpan Timeout
		{
			get => _timeout;
			set
			{
				if (value.TotalSeconds > 0 && value.TotalSeconds < 5)
					throw new ArgumentOutOfRangeException(nameof(Timeout));
				_timeout = value;
			}
		}

		/// <summary>
		/// Server will only accept IP Addresses that are in the whitelist.
		/// If the whitelist is empty all IPs will be accepted unless they are blacklisted.
		/// </summary>
		public IList<IPAddress> WhiteList { get; set; }

		/// <summary>
		/// The server will not accept connections from these IPAddresses.
		/// Whitelist has priority over Blacklist meaning that if an IPAddress is in the whitelist and blacklist
		/// It will still be added.
		/// </summary>
		public IList<IPAddress> BlackList { get; set; }

		internal IDictionary<int, IClientMetadata> ConnectedClients { get; set; }

		public IDictionary<string, EventHandler<ClientDataReceivedEventArgs>> DynamicCallbacks { get; protected set; }

		#endregion

		//Check if the server should allow the client that is attempting to connect.
		internal bool IsConnectionAllowed(IClientMetadata state)
		{
			if (WhiteList.Count > 0)
			{
				return CheckWhitelist(state.IPv4) || CheckWhitelist(state.IPv6);
			}

			if (BlackList.Count > 0)
			{
				return !CheckBlacklist(state.IPv4) && !CheckBlacklist(state.IPv6);
			}

			return true;
		}

		//Checks if an ip is in the whitelist
		protected bool CheckWhitelist(string ip)
		{
			var address = IPAddress.Parse(ip);
			return WhiteList.Any(x => Equals(x, address));
		}

		//Checks if an ip is in the blacklist
		protected bool CheckBlacklist(string ip)
		{
			var address = IPAddress.Parse(ip);
			return BlackList.Any(x => Equals(x, address));
		}

		protected SimpleServer(bool useSsl, SocketProtocolType protocol): base(useSsl,protocol) {
            Listening = false;
            ConnectedClients = new Dictionary<int, IClientMetadata>();
			DynamicCallbacks = new Dictionary<string, EventHandler<ClientDataReceivedEventArgs>>();
			BlackList = new List<IPAddress>();
			WhiteList = new List<IPAddress>();
        }

		/// <summary>
		/// Listen for clients on the IP:Port
		/// </summary>
		/// <param name="ip"></param>
		/// <param name="port"></param>
		/// <param name="limit"></param>
        public abstract void Listen(string ip, int port, int limit = 500);

		/// <summary>
		/// Listen for clients on all local ports for a given port.
		/// </summary>
		/// <param name="port"></param>
		/// <param name="limit"></param>
        public void Listen(int port , int limit = 500) => Listen("*", port, limit);

        protected IPAddress ListenerIPFromString(string ip) {
            try {

                if (string.IsNullOrEmpty(ip) || ip.Trim() == "*"){
                    var ipAdr = IPAddress.Any;
                    ListenerIp = ipAdr.ToString();
                    return ipAdr;
                }

                return IPAddress.Parse(ip);
            } catch (Exception ex ){
                SocketLogger?.Log("Invalid Server IP",ex,LogLevel.Fatal);
                return null;
            }
        }

		/// <summary>
		/// Returns true if a client is connected.
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
        public bool IsClientConnected(int id) {
            try {
                if (GetClientMetadataById(id) is IClientMetadata state && state.Listener is Socket socket) {
                    return !((socket.Poll(1000, SelectMode.SelectRead) && (socket.Available == 0)) || !socket.Connected);
                }
            } catch (ObjectDisposedException) {
                return false;
            }
            catch (Exception ex) {
                SocketLogger?.Log("Something went wrong trying to check if a client is connected.", ex, LogLevel.Error);
            }

            return false;
        }

		/// <summary>
		/// Shutdown a client.
		/// </summary>
		/// <param name="id"></param>
        public void ShutDownClient(int id) {
            ShutDownClient(id, DisconnectReason.Normal);
        }

        internal void ShutDownClient(int id, DisconnectReason reason) {
            var client = GetClientMetadataById(id);

            if (client == null)
                SocketLogger?.Log("Cannot shutdown client " + id + ", does not exist.", LogLevel.Warning);

            try {
                if (client?.Listener != null) {
                    client.Listener.Shutdown(SocketShutdown.Both);
                    client.Listener.Close();
                    client.Listener = null;
                }
            } catch (ObjectDisposedException de) {
                SocketLogger?.Log("Cannot shutdown client " + id + ", has already been disposed.",de, LogLevel.Warning);
            } catch (Exception ex) {
                SocketLogger?.Log("An error occurred when shutting down client " + id, ex, LogLevel.Warning);
            } finally {
                lock(ConnectedClients) {
                    ConnectedClients.Remove(id);
                    OnClientDisconnected(new ClientDisconnectedEventArgs(client, reason));
                }
            }
        }

        internal IClientMetadata GetClientMetadataById(int id) {
            return ConnectedClients.TryGetValue(id, out var state) ? state : null;
        }

		/// <summary>
		/// Get ClientInfo by id.
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
        public IClientInfo GetClientInfoById(int id) {
            return GetClientMetadataById(id);
        }

		/// <summary>
		/// Get ClientInfo by guid.
		/// </summary>
		/// <param name="guid"></param>
		/// <returns></returns>
		public IClientInfo GetClientInfoByGuid(string guid) {
			return ConnectedClients?.Values.Single(x => x.Guid == guid);
		}

		/// <summary>
		/// Get clientinfo of all connected clients
		/// </summary>
		/// <returns></returns>
		public IList<IClientInfo> GetAllClients() {
			var clients = ConnectedClients.Values.Cast<IClientInfo>().ToList();
			if (clients == null)
				return new List<IClientInfo>();
			return clients;
		}

		#region Data-Invokers

		internal virtual void OnMessageReceivedHandler(IClientMetadata client, SimpleMessage message) {

			Statistics?.AddReceivedMessages(1);

			var extraInfo = message.BuildInternalInfoFromBytes();
			var eventHandler = message.GetDynamicCallbackServer(extraInfo, DynamicCallbacks);

			if (message.MessageType == MessageType.Message) {
				var ev = new ClientMessageReceivedEventArgs(message.BuildDataToString(), client, message.BuildMetadataFromBytes());

				if (eventHandler != null)
					eventHandler?.Invoke(this, ev);
				else
					OnClientMessageReceived(ev);
			}

			if (message.MessageType == MessageType.Object) {
				var obj = message.BuildObjectFromBytes(extraInfo, out var type);

				if (obj == null || type == null) {
					var ev = new ClientObjectReceivedEventArgs(obj, type, client, message.BuildMetadataFromBytes());
					if (eventHandler != null)
						eventHandler?.Invoke(this, ev);
					else
						OnClientObjectReceived(ev);
				} else
					SocketLogger?.Log("Error receiving an object.", LogLevel.Error);
			}

			if (message.MessageType == MessageType.Bytes) {
				var ev = new ClientBytesReceivedEventArgs(client, message.Data, message.BuildMetadataFromBytes());
				if (eventHandler != null)
					eventHandler?.Invoke(this, ev);
				else
					OnClientBytesReceived(ev);
			}

			if (message.MessageType == MessageType.Auth) {
				var data = Encoding.UTF8.GetString(message.Data);
				var split = data.Split('|');

				client.ClientName = split[0];
				client.Guid = split[1];
				client.UserDomainName = split[2];
				client.OsVersion = split[3];
			}

		}

		#endregion

		#region Sending Data

		protected abstract void SendToSocket(int clientId, byte[] payload);

		protected abstract Task<bool> SendToSocketAsync(int clientId, byte[] payload);

		protected bool SendInternal(int clientid, MessageType msgType, byte[] data, IDictionary<object,object> metadata, IDictionary<object,object> extraInfo, string eventKey, EncryptionType eType, CompressionType cType) {

			try
			{
				if (EncryptionMethod != EncryptionType.None && (EncryptionPassphrase == null || EncryptionPassphrase.Length == 0))
					SocketLogger?.Log($"Please set a valid encryptionmethod when trying to encrypt a message.{Environment.NewLine}This message will not be encrypted.", LogLevel.Warning);

				if (eventKey != string.Empty && eventKey != null)
				{
					if (extraInfo == null)
						extraInfo = new Dictionary<object, object>();
					extraInfo.Add("DynamicCallback", eventKey);
				}

				var payload = MessageBuilder.Initialize(msgType, SocketLogger)
					.AddCompression(cType)
					.AddEncryption(EncryptionPassphrase, eType)
					.AddMessageBytes(data)
					.AddMetadata(metadata)
					.AddAdditionalInternalInfo(extraInfo)
					.BuildMessage();

				SendToSocket(clientid, payload);

				return true;
			}
			catch (Exception) {
				return false;
			}
		}

		protected async Task<bool> SendInternalAsync(int clientId, MessageType msgType, byte[] data, IDictionary<object, object> metadata, IDictionary<object, object> extraInfo, string eventKey, EncryptionType eType, CompressionType cType) {
			try
			{
				if (EncryptionMethod != EncryptionType.None && (EncryptionPassphrase == null || EncryptionPassphrase.Length == 0))
					SocketLogger?.Log($"Please set a valid encryptionmethod when trying to encrypt a message.{Environment.NewLine}This message will not be encrypted.", LogLevel.Warning);

				if (eventKey != string.Empty && eventKey != null)
				{
					if (extraInfo == null)
						extraInfo = new Dictionary<object, object>();
					extraInfo.Add("DynamicCallback", eventKey);
				}

				var payload = MessageBuilder.Initialize(msgType, SocketLogger)
					.AddCompression(cType)
					.AddEncryption(EncryptionPassphrase, eType)
					.AddMessageBytes(data)
					.AddMetadata(metadata)
					.AddAdditionalInternalInfo(extraInfo)
					.BuildMessage();

				return await SendToSocketAsync(clientId, payload);
			}
			catch (Exception) {
				return false;
			}
		}

		#region Message

		/// <summary>
		/// Send a message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendMessage(int clientId, string message, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression) {
			return SendInternal(clientId, MessageType.Message, Encoding.UTF8.GetBytes(message), metadata, null, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send a message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public bool SendMessage(int clientId, string message, IDictionary<object, object> metadata, string dynamicEventKey) {
			return SendMessage(clientId, message, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send a message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendMessage(int clientId, string message, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression)
		{
			return SendMessage(clientId, message, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send a message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public bool SendMessage(int clientId, string message, IDictionary<object, object> metadata)
		{
			return SendMessage(clientId, message, metadata, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send a message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <returns></returns>
		public bool SendMessage(int clientId, string message)
		{
			return SendMessage(clientId, message, null);
		}

		/// <summary>
		/// Send an async message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendMessageAsync(int clientId, string message, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression) {
			return await SendInternalAsync(clientId, MessageType.Message, Encoding.UTF8.GetBytes(message), metadata, null, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send an async message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public async Task<bool> SendMessageAsync(int clientId, string message, IDictionary<object, object> metadata, string dynamicEventKey) {
			return await SendMessageAsync(clientId, message, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send an async message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendMessageAsync(int clientId, string message, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression) {
			return await SendMessageAsync(clientId, message, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send an async message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public async Task<bool> SendMessageAsync(int clientId, string message, IDictionary<object, object> metadata) {
			return await SendMessageAsync(clientId, message, metadata, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send an async message to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="message"></param>
		/// <returns></returns>
		public async Task<bool> SendMessageAsync(int clientId, string message) {
			return await SendMessageAsync(clientId, message);
		}

		#endregion

		#region Bytes

		/// <summary>
		/// Send bytes to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendBytes(int clientId, byte[] data, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression) {
			return SendInternal(clientId, MessageType.Bytes, data, metadata, null, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send bytes to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendBytes(int clientId, byte[] data, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression) {
			return SendBytes(clientId, data, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send bytes to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public bool SendBytes(int clientId, byte[] data, IDictionary<object, object> metadata, string dynamicEventKey) {
			return SendBytes(clientId, data, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send bytes to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public bool SendBytes(int clientId, byte[] data, IDictionary<object, object> metadata) {
			return SendBytes(clientId, data, metadata, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send bytes to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <returns></returns>
		public bool SendBytes(int clientId, byte[] data) {
			return SendBytes(clientId, data);
		}

		/// <summary>
		/// Send bytes async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendBytesAsync(int clientId, byte[] data, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression) {
			return await SendInternalAsync(clientId, MessageType.Bytes, data, metadata, null, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send bytes async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendBytesAsync(int clientId, byte[] data, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression)
		{
			return await SendBytesAsync(clientId, data, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send bytes async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public async Task<bool> SendBytesAsync(int clientId, byte[] data, IDictionary<object, object> metadata, string dynamicEventKey) {
			return await SendBytesAsync(clientId, data, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send bytes async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public async Task<bool> SendBytesAsync(int clientId, byte[] data, IDictionary<object, object> metadata) {
			return await SendBytesAsync(clientId, data, metadata, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send bytes async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="data"></param>
		/// <returns></returns>
		public async Task<bool> SendBytesAsync(int clientId, byte[] data) {
			return await SendBytesAsync(clientId, data, null);
		}

		#endregion

		#region Objects

		/// <summary>
		/// Send object to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendObject(int clientId, object obj, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression) {
			var bytes = SerializationHelper.SerializeObjectToBytes(obj);

			var info = new Dictionary<object, object>();
			info.Add("Type", obj.GetType());

			return SendInternal(clientId, MessageType.Object, bytes, metadata, info, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send object to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public bool SendObject(int clientId, object obj, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression) {
			return SendObject(clientId, obj, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send object to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public bool SendObject(int clientId, object obj, IDictionary<object, object> metadata) {
			return SendObject(clientId, obj, metadata, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send object to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public bool SendObject(int clientId, object obj, IDictionary<object, object> metadata, string dynamicEventKey) {
			return SendObject(clientId, obj, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send object to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <returns></returns>
		public bool SendObject(int clientId, object obj) {
			return SendObject(clientId, obj, null);
		}

		/// <summary>
		/// Send object async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendObjectAsync(int clientId, object obj, IDictionary<object, object> metadata, string dynamicEventKey, EncryptionType encryption, CompressionType compression)
		{
			var bytes = SerializationHelper.SerializeObjectToBytes(obj);

			var info = new Dictionary<object, object>();
			info.Add("Type", obj.GetType());

			return await SendInternalAsync(clientId, MessageType.Bytes, bytes, metadata, info, dynamicEventKey, encryption, compression);
		}

		/// <summary>
		/// Send object async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="encryption"></param>
		/// <param name="compression"></param>
		/// <returns></returns>
		public async Task<bool> SendObjectAsync(int clientId, object obj, IDictionary<object, object> metadata, EncryptionType encryption, CompressionType compression)
		{
			return await SendObjectAsync(clientId, obj, metadata, string.Empty, encryption, compression);
		}

		/// <summary>
		/// Send object async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <returns></returns>
		public async Task<bool> SendObjectAsync(int clientId, object obj, IDictionary<object, object> metadata) {
			return await SendObjectAsync(clientId, obj, metadata, EncryptionMethod,CompressionMethod);
		}

		/// <summary>
		/// Send object async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <param name="metadata"></param>
		/// <param name="dynamicEventKey"></param>
		/// <returns></returns>
		public async Task<bool> SendObjectAsync(int clientId, object obj, IDictionary<object, object> metadata, string dynamicEventKey) {
			return await SendObjectAsync(clientId, obj, metadata, dynamicEventKey, EncryptionMethod, CompressionMethod);
		}

		/// <summary>
		/// Send object async to a client.
		/// </summary>
		/// <param name="clientId"></param>
		/// <param name="obj"></param>
		/// <returns></returns>
		public async Task<bool> SendObjectAsync(int clientId, object obj) {
			return await SendObjectAsync(clientId, obj, null);
		}

		#endregion

		#endregion


	}

}