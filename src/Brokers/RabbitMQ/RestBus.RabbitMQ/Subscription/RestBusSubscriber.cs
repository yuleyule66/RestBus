using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Framing;
using RestBus.Common;
using RestBus.Common.Amqp;
using RestBus.RabbitMQ.ChannelPooling;
using RestBus.RabbitMQ.Consumer;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace RestBus.RabbitMQ.Subscription
{
    //TODO: Describe what this class does.
    public class RestBusSubscriber : IRestBusSubscriber
    {
        volatile AmqpChannelPooler _subscriberPool;
        volatile AmqpModelContainer workChannel;
        volatile AmqpModelContainer subscriberChannel;
        volatile ConcurrentQueueingConsumer workConsumer;
        volatile ConcurrentQueueingConsumer subscriberConsumer;
        volatile CancellationTokenSource connectionBroken;
        volatile CancellationTokenSource stopWaitingOnQueue;
        readonly ManualResetEventSlim requestQueued = new ManualResetEventSlim();
        readonly string[] subscriberIdHeader;
        readonly IMessageMapper messageMapper;
        readonly MessagingConfiguration messagingConfig;
        readonly object exchangeDeclareSync = new object();
        readonly string serviceName;
        readonly string userName;
        readonly string password;
        readonly InterlockedBoolean hasStarted;
        volatile bool disposed = false;
        readonly CancellationTokenSource disposedCancellationSource = new CancellationTokenSource();
        ConcurrentQueueingConsumer lastProcessedConsumerQueue = null;
        readonly ConnectionFactory connectionFactory;

        //TODO: Consider moving this to a helper class.
        static string[] TRUE_STRING_ARRAY = new string[] { true.ToString() };

        /// <summary>
        /// Initislizes a new instance of the <see cref="RestBusSubscriber"/>
        /// </summary>
        /// <param name="messageMapper">The <see cref="IMessageMapper"/> used by the subscriber.</param>
        public RestBusSubscriber(IMessageMapper messageMapper) : this(messageMapper, null)
        {
        }

        /// <summary>
        /// Initislizes a new instance of the <see cref="RestBusSubscriber"/>
        /// </summary>
        /// <param name="messageMapper">The <see cref="IMessageMapper"/> used by the subscriber.</param>
        /// <param name="settings">The subscriber settings</param>
        public RestBusSubscriber(IMessageMapper messageMapper, SubscriberSettings settings)
        {
            this.messageMapper = messageMapper;
            messagingConfig = messageMapper.MessagingConfig; //Fetched only once
            if (messagingConfig == null) throw new ArgumentException("messageMapper.MessagingConfig returned null", "messageMapper");

            if (messageMapper.SupportedExchangeKinds == default(ExchangeKind))
            {
                throw new ArgumentException("messageMapper.SupportedExchangeKinds is not set up.", "messageMapper");
            }

            //TODO: Confirm service name is valid

            serviceName = (messageMapper.GetServiceName(null) ?? String.Empty).Trim();
            serviceName = (messageMapper.GetServiceName(null) ?? String.Empty).Trim();

            subscriberIdHeader = new string[] { AmqpUtils.GetNewExclusiveQueueId() };

            AmqpConnectionInfo.EnsureValid(messageMapper.ServerUris, "messageMapper.ServerUris");
            this.connectionFactory = new ConnectionFactory();
            connectionFactory.Uri = messageMapper.ServerUris[0].Uri;
            connectionFactory.UserName = messageMapper.ServerUris[0].UserName;
            connectionFactory.Password = messageMapper.ServerUris[0].Password;
            ConnectionNames = messageMapper.ServerUris.Select(u => u.FriendlyName ?? String.Empty).ToArray();
            connectionFactory.RequestedHeartbeat = Client.RPCStrategyHelpers.HEART_BEAT;

            this.Settings = settings ?? new SubscriberSettings(); //Make sure a default value is set, if not supplied by user.
            this.Settings.Subscriber = this; //Indicate that the subcriber settings is owned by this subscriber.
        }

        public string Id
        {
            get { return subscriberIdHeader[0]; }
        }

        public void Start()
        {
            if (!hasStarted.SetTrueIf(false))
            {
                throw new InvalidOperationException("RestBus Subscriber has already started!");
            }

            Restart();
        }

        public SubscriberSettings Settings { get; }

        internal bool HasStarted
        {
            get
            {
                return hasStarted;
            }
        }

        public IList<string> ConnectionNames { get; private set; }

        public void Restart()
        {
            hasStarted.Set(true);

            //CLose connections and channels
            if (subscriberChannel != null)
            {
                if (subscriberConsumer != null)
                {
                    try
                    {
                        subscriberChannel.Channel.BasicCancel(subscriberConsumer.ConsumerTag);
                    }
                    catch { }
                }

                try
                {
                    subscriberChannel.Close();
                }
                catch
                {
                }
            }

            if (workChannel != null)
            {
                if (workConsumer != null)
                {
                    try
                    {
                        workChannel.Channel.BasicCancel(workConsumer.ConsumerTag);
                    }
                    catch { }
                }

                try
                {
                    workChannel.Close();
                }
                catch
                {
                }
            }

            if (_subscriberPool != null)
            {
                _subscriberPool.Dispose();
            }

            //NOTE: CreateConnection() can throw BrokerUnreachableException
            //That's okay because the exception needs to propagate to Reconnect() or Start()
            var conn = connectionFactory.CreateConnection();

            if (connectionBroken != null) connectionBroken.Dispose();
            connectionBroken = new CancellationTokenSource();

            if (stopWaitingOnQueue != null) stopWaitingOnQueue.Dispose();
            stopWaitingOnQueue = CancellationTokenSource.CreateLinkedTokenSource(disposedCancellationSource.Token, connectionBroken.Token);

            var pool = new AmqpChannelPooler(conn);
            _subscriberPool = pool;

            //Use pool reference henceforth.

            //Create work channel and declare exchanges and queues
            workChannel = pool.GetModel(ChannelFlags.Consumer);

            //Redeclare exchanges and queues
            AmqpUtils.DeclareExchangeAndQueues(workChannel.Channel, messageMapper, messagingConfig, serviceName, exchangeDeclareSync, Id);

            //Listen on work queue
            workConsumer = new ConcurrentQueueingConsumer(workChannel.Channel, requestQueued);
            string workQueueName = AmqpUtils.GetWorkQueueName(messagingConfig, serviceName);

            workChannel.Channel.BasicQos(0, (ushort)Settings.PrefetchCount, false);
            workChannel.Channel.BasicConsume(workQueueName, Settings.AckBehavior == SubscriberAckBehavior.Automatic, workConsumer);

            //Listen on subscriber queue
            subscriberChannel = pool.GetModel(ChannelFlags.Consumer);
            subscriberConsumer = new ConcurrentQueueingConsumer(subscriberChannel.Channel, requestQueued);
            string subscriberWorkQueueName = AmqpUtils.GetSubscriberQueueName(serviceName, Id);

            subscriberChannel.Channel.BasicQos(0, (ushort)Settings.PrefetchCount, false);
            subscriberChannel.Channel.BasicConsume(subscriberWorkQueueName, Settings.AckBehavior == SubscriberAckBehavior.Automatic, subscriberConsumer);

            //Cancel connectionBroken on connection/consumer problems 
            pool.Connection.ConnectionShutdown += (s, e) => { connectionBroken.Cancel(); };
            workConsumer.ConsumerCancelled += (s, e) => { connectionBroken.Cancel(); };
            subscriberConsumer.ConsumerCancelled += (s, e) => { connectionBroken.Cancel(); };
        }

        //Will block until a request is received from either queue
        public MessageContext Dequeue()
        {
            if (disposed) throw new ObjectDisposedException(GetType().FullName);
            if (workConsumer == null || subscriberConsumer == null) throw new InvalidOperationException("Start the subscriber prior to calling Dequeue");

            //TODO: Test what happens if either of these consumers are cancelled by the server, should consumer.Cancelled be handled?
            //In that scenario, requestQueued.Wait below should throw an exception and try to reconnect.

            HttpRequestPacket request;
            MessageDispatch dispatch;

            ConcurrentQueueingConsumer queue1 = null, queue2 = null;

            while (true)
            {
                if (disposed) throw new ObjectDisposedException(GetType().FullName);
                if (lastProcessedConsumerQueue == subscriberConsumer)
                {
                    queue1 = workConsumer;
                    queue2 = subscriberConsumer;
                }
                else
                {
                    queue1 = subscriberConsumer;
                    queue2 = workConsumer;
                }

                try
                {
                    if (TryGetRequest(queue1, out request, out dispatch))
                    {
                        lastProcessedConsumerQueue = queue1;
                        break;
                    }

                    if (TryGetRequest(queue2, out request, out dispatch))
                    {
                        lastProcessedConsumerQueue = queue2;
                        break;
                    }
                }
                catch (Exception e)
                {
                    //TODO: Log this -- no exception is expected from the calls in the try block.

                    throw;
                }

                try
                {
                    requestQueued.Wait(stopWaitingOnQueue.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!disposed && connectionBroken.IsCancellationRequested)
                    {
                        //Connection broken or consumer has been cancelled but client is not disposed
                        //So reconnect
                        Reconnect();
                    }
                    else
                    {
                        throw;
                    }
                }

                requestQueued.Reset();
            }

            return new MessageContext
            {
                Request = request,
                ReplyToQueue = dispatch.Delivery.BasicProperties == null ? null : dispatch.Delivery.BasicProperties.ReplyTo,
                CorrelationId = dispatch.Delivery.BasicProperties.CorrelationId,
                Dispatch = dispatch
            };


        }

        private bool TryGetRequest(ConcurrentQueueingConsumer consumer, out HttpRequestPacket request, out MessageDispatch dispatch)
        {
            request = null;
            dispatch = null;

            BasicDeliverEventArgs item;
            if (!consumer.TryInstantDequeue(out item, throwIfClosed: false))
            {
                return false;
            }

            //TODO: Pool MessageDispatch
            //Get message 
            dispatch = new MessageDispatch { Consumer = consumer, Delivery = item };

            //Deserialize message
            bool wasDeserialized = true;

            try
            {
                request = HttpRequestPacket.Deserialize(item.Body);
            }
            catch
            {
                wasDeserialized = false;
            }

            if (wasDeserialized)
            {
                //Add/Update Subscriber-Id header
                request.Headers[Common.Shared.SUBSCRIBER_ID_HEADER] = this.subscriberIdHeader;

                //Add redelivered header if item was redelivered.
                if (item.Redelivered)
                {
                    request.Headers[Common.Shared.REDELIVERED_HEADER] = TRUE_STRING_ARRAY;
                }
            }
            //Reject message if deserialization failed.
            else if (!wasDeserialized && Settings.AckBehavior != SubscriberAckBehavior.Automatic)
            {
                consumer.Model.BasicReject(item.DeliveryTag, false);
                return false;
            }

            return true;

        }

        public void Dispose()
        {
            disposed = true;
            disposedCancellationSource.Cancel();

            if (workChannel != null)
            {
                if (workConsumer != null)
                {
                    try
                    {
                        workChannel.Channel.BasicCancel(workConsumer.ConsumerTag);
                    }
                    catch { }
                }
                workChannel.Close();
            }

            if (subscriberChannel != null)
            {
                if (subscriberConsumer != null)
                {
                    try
                    {
                        subscriberChannel.Channel.BasicCancel(subscriberConsumer.ConsumerTag);
                    }
                    catch { }
                }
                subscriberChannel.Close();
            }

            if (_subscriberPool != null)
            {
                _subscriberPool.Dispose();
            }

            requestQueued.Dispose();
            disposedCancellationSource.Dispose();
            if (stopWaitingOnQueue != null) stopWaitingOnQueue.Dispose();
            if (connectionBroken != null) connectionBroken.Dispose();

        }

        private void Reconnect()
        {
            //Loop until a connection is made
            bool successfulRestart = false;
            while (true)
            {
                try
                {
                    Restart();
                    successfulRestart = true;
                }
                catch { }

                if (disposed) throw new ObjectDisposedException(GetType().FullName);

                if (successfulRestart) break;
                Thread.Sleep(1);
            }
        }

        public void SendResponse(MessageContext context, HttpResponsePacket response)
        {
            if (disposed) throw new ObjectDisposedException(GetType().FullName);

            var dispatch = context.Dispatch as MessageDispatch;
            if (dispatch != null)
            {
                //Ack request
                if (Settings.AckBehavior != SubscriberAckBehavior.Automatic && dispatch.Consumer.Model.IsOpen)
                {
                    dispatch.Consumer.Model.BasicAck(dispatch.Delivery.DeliveryTag, false);

                    //NOTE: The call above takes place in different threads silmultaneously
                    //In which case multiple threads will be using the same channel at the same time.
                    //It's okay in this case, because transmissions within a channel are synchronized, as seen in:
                    //https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/f16c093f6409e11d9d77115038cb224eb39468ec/projects/client/RabbitMQ.Client/src/client/impl/ModelBase.cs#L459
                    //and
                    //https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/f16c093f6409e11d9d77115038cb224eb39468ec/projects/client/RabbitMQ.Client/src/client/impl/SessionBase.cs#L177
                }
            }

            //Exit method if no replyToQueue was specified.
            if (String.IsNullOrEmpty(context.ReplyToQueue)) return;

            if (_subscriberPool.Connection == null)
            {
                //TODO: Log this -- it technically shouldn't happen. Also translate to a HTTP Unreachable because it means StartCallbackQueueConsumer didn't create a connection

                throw new InvalidOperationException("This is Bad");
            }

            //Add/Update Subscriber-Id header
            response.Headers[Common.Shared.SUBSCRIBER_ID_HEADER] = subscriberIdHeader;

            //Send response
            var pooler = _subscriberPool;
            AmqpModelContainer model = null;
            try
            {
                model = pooler.GetModel(ChannelFlags.None);
                BasicProperties basicProperties = new BasicProperties { CorrelationId = context.CorrelationId };

                model.Channel.BasicPublish(String.Empty,
                                context.ReplyToQueue,
                                basicProperties,
                                response.Serialize());
            }
            finally
            {
                if (model != null)
                {
                    model.Close();
                }
            }
        }
    }
}
