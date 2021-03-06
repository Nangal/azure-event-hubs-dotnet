﻿namespace Microsoft.Azure.EventHubs.Processor.UnitTests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class EventProcessorHostTests
    {
        ITestOutputHelper output;
        EventHubsConnectionStringBuilder ConnectionStringBuilder;
        string StorageConnectionString;
        string EventHubConnectionString;
        string LeaseContainerName;
        string[] PartitionIds;

        public EventProcessorHostTests(ITestOutputHelper output)
        {
            this.output = output;

            string eventHubConnectionString = Environment.GetEnvironmentVariable("EVENTHUBCONNECTIONSTRING");
            if (string.IsNullOrWhiteSpace(eventHubConnectionString))
            {
                throw new InvalidOperationException("EVENTHUBCONNECTIONSTRING environment variable was not found!");
            }

            string storageConnectionString = Environment.GetEnvironmentVariable("EVENTPROCESSORSTORAGECONNECTIONSTRING");
            if (string.IsNullOrWhiteSpace(eventHubConnectionString))
            {
                throw new InvalidOperationException("EVENTPROCESSORSTORAGECONNECTIONSTRING environment variable was not found!");
            }

            this.ConnectionStringBuilder = new EventHubsConnectionStringBuilder(eventHubConnectionString);
            this.StorageConnectionString = storageConnectionString;
            this.EventHubConnectionString = eventHubConnectionString;

            // Use entity name as lease container name.
            // Convert to lowercase in case there is capital letter in the entity path.
            // Uppercase is invalid for Azure Storage container names.
            this.LeaseContainerName = this.ConnectionStringBuilder.EntityPath.ToLower();

            // Discover partition ids.
            PartitionIds = this.GetPartitionIdsAsync(this.ConnectionStringBuilder.ToString()).Result;
            Log($"EventHub has {PartitionIds.Length} partitions");
        }

        /// <summary>
        /// Validating cases where entity path is provided through eventHubPath and EH connection string parameters
        /// on the EPH constructor.
        /// </summary>
        [Fact]
        void ProcessorHostEntityPathSetting()
        {
            var csb = new EventHubsConnectionStringBuilder(this.EventHubConnectionString)
            {
                EntityPath = "myeh"
            };

            // Entity path provided in the connection string.
            Log("Testing condition: Entity path provided in the connection string only.");
            var eventProcessorHost = new EventProcessorHost(
                null,
                PartitionReceiver.DefaultConsumerGroupName,
                csb.ToString(),
                this.StorageConnectionString,
                this.LeaseContainerName);
            Assert.Equal("myeh", eventProcessorHost.EventHubPath);

            // Entity path provided in the eventHubPath parameter.
            Log("Testing condition: Entity path provided in the eventHubPath only.");
            csb.EntityPath = null;
            eventProcessorHost = new EventProcessorHost(
                "myeh2",
                PartitionReceiver.DefaultConsumerGroupName,
                csb.ToString(),
                this.StorageConnectionString,
                this.LeaseContainerName);
            Assert.Equal("myeh2", eventProcessorHost.EventHubPath);

            // The same entity path provided in both eventHubPath parameter and the connection string.
            Log("Testing condition: The same entity path provided in the eventHubPath and connection string.");
            csb.EntityPath = "mYeH";
            eventProcessorHost = new EventProcessorHost(
                "myeh",
                PartitionReceiver.DefaultConsumerGroupName,
                csb.ToString(),
                this.StorageConnectionString,
                this.LeaseContainerName);
            Assert.Equal("myeh", eventProcessorHost.EventHubPath);

            // Entity path not provided in both eventHubPath and the connection string.
            Log("Testing condition: Entity path not provided in both eventHubPath and connection string.");
            try
            {
                csb.EntityPath = null;
                new EventProcessorHost(
                    string.Empty,
                    PartitionReceiver.DefaultConsumerGroupName,
                    csb.ToString(),
                    this.StorageConnectionString,
                    this.LeaseContainerName);
                throw new Exception("Entity path wasn't provided and this new call was supposed to fail");
            }
            catch (ArgumentException)
            {
                Log("Caught ArgumentException as expected.");
            }

            // Entity path conflict.
            Log("Testing condition: Entity path conflict.");
            try
            {
                csb.EntityPath = "myeh";
                new EventProcessorHost(
                    "myeh2",
                    PartitionReceiver.DefaultConsumerGroupName,
                    csb.ToString(),
                    this.StorageConnectionString,
                    this.LeaseContainerName);
                throw new Exception("Entity path values conflict and this new call was supposed to fail");
            }
            catch (ArgumentException)
            {
                Log("Caught ArgumentException as expected.");
            }
        }
        [Fact]
        Task SingleProcessorHost()
        {
            var eventProcessorHost = new EventProcessorHost(
                null,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                this.LeaseContainerName);

            return RunGenericScenario(eventProcessorHost);
        }

        [Fact]
        async Task HostReregisterShouldFail()
        {
            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                this.LeaseContainerName);

            // Calling register for the first time should succeed.
            Log("Registering EventProcessorHost for the first time.");
            await eventProcessorHost.RegisterEventProcessorAsync<TestEventProcessor>();

            try
            {
                // Calling register for the second time should fail.
                Log("Registering EventProcessorHost for the second time which should fail.");
                await eventProcessorHost.RegisterEventProcessorAsync<TestEventProcessor>();
                throw new InvalidOperationException("Second RegisterEventProcessorAsync call should have failed.");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("A PartitionManager cannot be started multiple times."))
                {
                    Log($"Caught {ex.GetType()} as expected");
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                await eventProcessorHost.UnregisterEventProcessorAsync();
            }
        }

        [Fact]
        async Task MultipleProcessorHosts()
        {
            Log("Testing with 2 EventProcessorHost instances");

            var partitionReceiveEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();
            foreach (var partitionId in PartitionIds)
            {
                partitionReceiveEvents[partitionId] = new AsyncAutoResetEvent(false);
            }

            int hostCount = 2;
            var hosts = new List<EventProcessorHost>();
            try
            {
                for (int i = 0; i < hostCount; i++)
                {
                Log("Creating EventProcessorHost");
                    var eventProcessorHost = new EventProcessorHost(
                        string.Empty,
                        PartitionReceiver.DefaultConsumerGroupName,
                        this.EventHubConnectionString,
                        this.StorageConnectionString,
                        this.LeaseContainerName);
                    hosts.Add(eventProcessorHost);
                Log($"Calling RegisterEventProcessorAsync");
                    var processorOptions = new EventProcessorOptions
                    {
                        ReceiveTimeout = TimeSpan.FromSeconds(10),
                        InvokeProcessorAfterReceiveTimeout = true
                    };

                    var processorFactory = new TestEventProcessorFactory();
                    processorFactory.OnCreateProcessor += (f, createArgs) =>
                    {
                        var processor = createArgs.Item2;
                        string partitionId = createArgs.Item1.PartitionId;
                        string hostName = createArgs.Item1.Owner;
                    processor.OnOpen += (_, partitionContext) => Log($"{hostName} > Partition {partitionId} TestEventProcessor opened");
                    processor.OnClose += (_, closeArgs) => Log($"{hostName} > Partition {partitionId} TestEventProcessor closing: {closeArgs.Item2}");
                    processor.OnProcessError += (_, errorArgs) => Log($"{hostName} > Partition {partitionId} TestEventProcessor process error {errorArgs.Item2.Message}");
                        processor.OnProcessEvents += (_, eventsArgs) =>
                        {
                            int eventCount = eventsArgs.Item2 != null ? eventsArgs.Item2.events.Count() : 0;
                        Log($"{hostName} > Partition {partitionId} TestEventProcessor processing {eventCount} event(s)");
                            if (eventCount > 0)
                            {
                                var receivedEvent = partitionReceiveEvents[partitionId];
                                receivedEvent.Set();
                            }
                        };
                    };

                    await eventProcessorHost.RegisterEventProcessorFactoryAsync(processorFactory, processorOptions);
                }

            Log("Waiting for partition ownership to settle...");
                await Task.Delay(TimeSpan.FromSeconds(30));

            Log("Sending an event to each partition");
                var sendTasks = new List<Task>();
                foreach (var partitionId in PartitionIds)
                {
                    sendTasks.Add(this.SendToPartitionAsync(partitionId, $"{partitionId} event.", this.ConnectionStringBuilder.ToString()));
                }
                await Task.WhenAll(sendTasks);

            Log("Verifying an event was received by each partition");
                foreach (var partitionId in PartitionIds)
                {
                    var receivedEvent = partitionReceiveEvents[partitionId];
                    bool partitionReceivedMessage = await receivedEvent.WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.True(partitionReceivedMessage, $"Partition {partitionId} didn't receive any message!");
                }
            }
            finally
            {
                var shutdownTasks = new List<Task>();
                foreach (var host in hosts)
                {
                Log($"Host {host} Calling UnregisterEventProcessorAsync.");
                    shutdownTasks.Add(host.UnregisterEventProcessorAsync());
                }

                await Task.WhenAll(shutdownTasks);
            }
        }

        [Fact]
        async Task InvokeAfterReceiveTimeoutTrue()
        {
            const int ReceiveTimeoutInSeconds = 15;

            Log("Testing EventProcessorHost with InvokeProcessorAfterReceiveTimeout=true");

            var emptyBatchReceiveEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();
            foreach (var partitionId in PartitionIds)
            {
                emptyBatchReceiveEvents[partitionId] = new AsyncAutoResetEvent(false);
            }

            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                this.LeaseContainerName);

            var processorOptions = new EventProcessorOptions {
                ReceiveTimeout = TimeSpan.FromSeconds(ReceiveTimeoutInSeconds),
                InvokeProcessorAfterReceiveTimeout = true
            };

            var processorFactory = new TestEventProcessorFactory();
            processorFactory.OnCreateProcessor += (f, createArgs) =>
            {
                var processor = createArgs.Item2;
                string partitionId = createArgs.Item1.PartitionId;
                processor.OnOpen += (_, partitionContext) => Log($"Partition {partitionId} TestEventProcessor opened");
                processor.OnProcessEvents += (_, eventsArgs) =>
                {
                    int eventCount = eventsArgs.Item2.events != null ? eventsArgs.Item2.events.Count() : 0;
                    Log($"Partition {partitionId} TestEventProcessor processing {eventCount} event(s)");
                    if (eventCount == 0)
                    {
                        var emptyBatchReceiveEvent = emptyBatchReceiveEvents[partitionId];
                        emptyBatchReceiveEvent.Set();
                    }
                };
            };

            await eventProcessorHost.RegisterEventProcessorFactoryAsync(processorFactory, processorOptions);
            try
            {
                Log("Waiting for each partition to receive an empty batch of events...");
                foreach (var partitionId in PartitionIds)
                {
                    var emptyBatchReceiveEvent = emptyBatchReceiveEvents[partitionId];
                    bool emptyBatchReceived = await emptyBatchReceiveEvent.WaitAsync(TimeSpan.FromSeconds(ReceiveTimeoutInSeconds * 2));
                    Assert.True(emptyBatchReceived, $"Partition {partitionId} didn't receive an empty batch!");
                }
            }
            finally
            {
                Log("Calling UnregisterEventProcessorAsync");
                await eventProcessorHost.UnregisterEventProcessorAsync();
            }
        }

        [Fact]
        async Task InvokeAfterReceiveTimeoutFalse()
        {
            const int ReceiveTimeoutInSeconds = 15;

            Log("Calling RegisterEventProcessorAsync with InvokeProcessorAfterReceiveTimeout=false");

            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                this.LeaseContainerName);

            var processorOptions = new EventProcessorOptions
            {
                ReceiveTimeout = TimeSpan.FromSeconds(ReceiveTimeoutInSeconds),
                InvokeProcessorAfterReceiveTimeout = false
            };

            var emptyBatchReceiveEvent = new AsyncAutoResetEvent(false);
            var processorFactory = new TestEventProcessorFactory();
            processorFactory.OnCreateProcessor += (f, createArgs) =>
            {
                var processor = createArgs.Item2;
                string partitionId = createArgs.Item1.PartitionId;
                processor.OnProcessEvents += (_, eventsArgs) =>
                {
                    int eventCount = eventsArgs.Item2 != null ? eventsArgs.Item2.events.Count() : 0;
                    Log($"Partition {partitionId} TestEventProcessor processing {eventCount} event(s)");
                    if (eventCount == 0)
                    {
                        emptyBatchReceiveEvent.Set();
                    }
                };
            };

            await eventProcessorHost.RegisterEventProcessorFactoryAsync(processorFactory, processorOptions);
            try
            {
                Log("Verifying no empty batches arrive...");
                bool waitSucceeded = await emptyBatchReceiveEvent.WaitAsync(TimeSpan.FromSeconds(ReceiveTimeoutInSeconds * 2));
                Assert.False(waitSucceeded, "No empty batch should have been received!");
            }
            finally
            {
                Log("Calling UnregisterEventProcessorAsync");
                await eventProcessorHost.UnregisterEventProcessorAsync();
            }
        }

        /// <summary>
        /// This test requires a eventhub with consumer groups $Default and cgroup1.
        /// </summary>
        /// <returns></returns>
        [Fact]
        async Task MultipleConsumerGroups()
        {
            var customConsumerGroupName = "cgroup1";

            // Generate a new lease container name that will be used through out the test.
            string leaseContainerName = Guid.NewGuid().ToString();

            var consumerGroupNames = new[]  { PartitionReceiver.DefaultConsumerGroupName, customConsumerGroupName };
            var processorOptions = new EventProcessorOptions { ReceiveTimeout = TimeSpan.FromSeconds(15) };
            var processorFactory = new TestEventProcessorFactory();
            var partitionReceiveEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();
            var hosts = new List<EventProcessorHost>();

            // Confirm that custom consumer group exists before starting hosts.
            try
            {
                // Create a receiver on the consumer group and try to receive.
                // Receive call will fail if consumer group is missing.
                var ehClient = EventHubClient.CreateFromConnectionString(this.EventHubConnectionString);
                var receiver = ehClient.CreateReceiver(customConsumerGroupName, this.PartitionIds.First(), PartitionReceiver.StartOfStream);
                await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5));
            }
            catch (MessagingEntityNotFoundException)
            {
                throw new Exception($"Cunsumer group {customConsumerGroupName} cannot be found. MultipleConsumerGroups unit test requires consumer group '{customConsumerGroupName}' to be created before running the test.");
            }

            processorFactory.OnCreateProcessor += (f, createArgs) =>
            {
                var processor = createArgs.Item2;
                string partitionId = createArgs.Item1.PartitionId;
                string hostName = createArgs.Item1.Owner;
                string consumerGroupName = createArgs.Item1.ConsumerGroupName;
                processor.OnOpen += (_, partitionContext) => Log($"{hostName} > {consumerGroupName} > Partition {partitionId} TestEventProcessor opened");
                processor.OnClose += (_, closeArgs) => Log($"{hostName} > {consumerGroupName} > Partition {partitionId} TestEventProcessor closing: {closeArgs.Item2}");
                processor.OnProcessError += (_, errorArgs) => Log($"{hostName} > {consumerGroupName} > Partition {partitionId} TestEventProcessor process error {errorArgs.Item2.Message}");
                processor.OnProcessEvents += (_, eventsArgs) =>
                {
                    int eventCount = eventsArgs.Item2 != null ? eventsArgs.Item2.events.Count() : 0;
                    Log($"{hostName} > {consumerGroupName} > Partition {partitionId} TestEventProcessor processing {eventCount} event(s)");
                    if (eventCount > 0)
                    {
                        var receivedEvent = partitionReceiveEvents[consumerGroupName + "-" + partitionId];
                        receivedEvent.Set();
                    }
                };
            };

            try
            {
                // Register a new host for each consumer group.
                foreach (var consumerGroupName in consumerGroupNames)
                {
                    var eventProcessorHost = new EventProcessorHost(
                        string.Empty,
                        consumerGroupName,
                        this.EventHubConnectionString,
                        this.StorageConnectionString,
                        leaseContainerName);

                Log($"Calling RegisterEventProcessorAsync on consumer group {consumerGroupName}");

                    foreach (var partitionId in PartitionIds)
                    {
                        partitionReceiveEvents[consumerGroupName + "-" + partitionId] = new AsyncAutoResetEvent(false);
                    }

                    await eventProcessorHost.RegisterEventProcessorFactoryAsync(processorFactory, processorOptions);
                    hosts.Add(eventProcessorHost);
                }

            Log("Sending an event to each partition");
                var sendTasks = new List<Task>();
                foreach (var partitionId in PartitionIds)
                {
                    sendTasks.Add(this.SendToPartitionAsync(partitionId, $"{partitionId} event.", this.ConnectionStringBuilder.ToString()));
                }

                await Task.WhenAll(sendTasks);

            Log("Verifying an event was received by each partition for each consumer group");
                foreach (var consumerGroupName in consumerGroupNames)
                {
                    foreach (var partitionId in PartitionIds)
                    {
                        var receivedEvent = partitionReceiveEvents[consumerGroupName + "-" + partitionId];
                        bool partitionReceivedMessage = await receivedEvent.WaitAsync(TimeSpan.FromSeconds(30));
                        Assert.True(partitionReceivedMessage, $"ConsumerGroup {consumerGroupName} > Partition {partitionId} didn't receive any message!");
                    }
                }

            Log("Success");
            }
            finally
            {
                Log("Calling UnregisterEventProcessorAsync on both hosts.");
                foreach (var eph in hosts)
                {
                    await eph.UnregisterEventProcessorAsync();
                }
            }
        }

        [Fact]
        async Task InitialOffsetProviderWithDateTime()
        {
            // Send and receive single message so we can find out enqueue date-time of the last message.
            var lastEvents = await SendAndReceiveSingleEvent();

            // We will use last enqueued message's enqueue date-time so EPH will pick messages only after that point.
            var lastEnqueueDateTime = lastEvents.Max(le => le.Value.SystemProperties.EnqueuedTimeUtc);
            Log($"Last message enqueued at {lastEnqueueDateTime}");

            // Use a randomly generated container name so that initial offset provider will be respected.
            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                Guid.NewGuid().ToString());

            var processorOptions = new EventProcessorOptions
            {
                ReceiveTimeout = TimeSpan.FromSeconds(15),
                InitialOffsetProvider = partitionId => lastEnqueueDateTime
            };

            var receivedEvents = await this.RunGenericScenario(eventProcessorHost, processorOptions);

            // We should have received only 1 event from each partition.
            Assert.False(receivedEvents.Any(kvp => kvp.Value.Count != 1), "One of the partitions didn't return exactly 1 event");
        }

        [Fact]
        async Task InitialOffsetProviderWithOffset()
        {
            // Send and receive single message so we can find out offset of the last message.
            var lastEvents = await SendAndReceiveSingleEvent();

            // Use a randomly generated container name so that initial offset provider will be respected.
            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                Guid.NewGuid().ToString());

            var processorOptions = new EventProcessorOptions
            {
                ReceiveTimeout = TimeSpan.FromSeconds(15),
                InitialOffsetProvider = partitionId => lastEvents[partitionId].SystemProperties.Offset
            };

            var receivedEvents = await this.RunGenericScenario(eventProcessorHost, processorOptions);

            // We should have received only 1 event from each partition.
            Assert.False(receivedEvents.Any(kvp => kvp.Value.Count != 1), "One of the partitions didn't return exactly 1 event");
        }

        [Fact]
        async Task InitialOffsetProviderOverrideBehavior()
        {
            // Generate a new lease container name that will be used through out the test.
            string leaseContainerName = Guid.NewGuid().ToString();

            // First host will send and receive as usual.
            var eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            await this.RunGenericScenario(eventProcessorHost);

            // Second host will use an initial offset provider.
            // Since we are still on the same lease container, initial offset provider shouldn't rule.
            // We should continue receiving where we left instead if start-of-stream where initial offset provider dictates.
            eventProcessorHost = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            var processorOptions = new EventProcessorOptions
            {
                ReceiveTimeout = TimeSpan.FromSeconds(15),
                InitialOffsetProvider = partitionId => PartitionReceiver.StartOfStream
            };
            var receivedEvents = await this.RunGenericScenario(eventProcessorHost, processorOptions);

            // We should have received only 1 event from each partition.
            Assert.False(receivedEvents.Any(kvp => kvp.Value.Count != 1), "One of the partitions didn't return exactly 1 event");
        }

        [Fact]
        async Task CheckpointEventDataShouldHold()
        {
            // Generate a new lease container name that will use through out the test.
            string leaseContainerName = Guid.NewGuid().ToString();

            // Consume all messages with first host.
            var eventProcessorHostFirst = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            await RunGenericScenario(eventProcessorHostFirst);

            // For the second time we initiate a host and this time it should pick from where the previous host left.
            // In other words, it shouldn't start receiving from start of the stream.
            var eventProcessorHostSecond = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            var receivedEvents = await RunGenericScenario(eventProcessorHostSecond);

            // We should have received only 1 event from each partition.
            Assert.False(receivedEvents.Any(kvp => kvp.Value.Count != 1), "One of the partitions didn't return exactly 1 event");
        }

        [Fact]
        async Task CheckpointBatchShouldHold()
        {
            // Generate a new lease container name that will use through out the test.
            string leaseContainerName = Guid.NewGuid().ToString();

            // Consume all messages with first host.
            var eventProcessorHostFirst = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            await RunGenericScenario(eventProcessorHostFirst, checkPointLastEvent: false, checkPointBatch: true);

            // For the second time we initiate a host and this time it should pick from where the previous host left.
            // In other words, it shouldn't start receiving from start of the stream.
            var eventProcessorHostSecond = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            var receivedEvents = await RunGenericScenario(eventProcessorHostSecond);

            // We should have received only 1 event from each partition.
            Assert.False(receivedEvents.Any(kvp => kvp.Value.Count != 1), "One of the partitions didn't return exactly 1 event");
        }

        /// <summary>
        /// If a host doesn't checkpoint on the processed events and shuts down, new host should start processing from the beginning.
        /// </summary>
        /// <returns></returns>
        [Fact]
        async Task NoCheckpointThenNewHostReadsFromStart()
        {
            // Generate a new lease container name that will be used through out the test.
            string leaseContainerName = Guid.NewGuid().ToString();

            // Consume all messages with first host.
            var eventProcessorHostFirst = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            var receivedEvents1 = await RunGenericScenario(eventProcessorHostFirst, checkPointLastEvent: false);
            var totalEventsFromFirstHost = receivedEvents1.Sum(part => part.Value.Count);

            // Second time we initiate a host, it should pick from where previous host left.
            // In other words, it shouldn't start receiving from start of the stream.
            var eventProcessorHostSecond = new EventProcessorHost(
                string.Empty,
                PartitionReceiver.DefaultConsumerGroupName,
                this.EventHubConnectionString,
                this.StorageConnectionString,
                leaseContainerName);
            var receivedEvents2 = await RunGenericScenario(eventProcessorHostSecond);
            var totalEventsFromSecondHost = receivedEvents2.Sum(part => part.Value.Count);

            // Second host should have received +partition-count messages.
            Assert.True(totalEventsFromFirstHost + PartitionIds.Count() == totalEventsFromSecondHost,
                $"Second host received {receivedEvents2} events where as first host receive {receivedEvents1} events.");
        }

        async Task<Dictionary<string, EventData>> SendAndReceiveSingleEvent()
        {
            // Send single event to each partition.
            Log("Sending an event to each partition");
            var sendTasks = new List<Task>();
            foreach (var partitionId in PartitionIds)
            {
                sendTasks.Add(this.SendToPartitionAsync(partitionId, $"{partitionId} event.", this.ConnectionStringBuilder.ToString()));
            }

            await Task.WhenAll(sendTasks);

            // Receive all events including last events from each partition.
            var ehClient = EventHubClient.CreateFromConnectionString(this.ConnectionStringBuilder.ToString());
            ConcurrentDictionary<string, EventData> lastEvents = new ConcurrentDictionary<string, EventData>();
            var receiveTasks = PartitionIds.Select(async partitionId =>
                {
                    var receiver = ehClient.CreateReceiver(PartitionReceiver.DefaultConsumerGroupName, partitionId, PartitionReceiver.StartOfStream);
                    while (true)
                    {
                        var messages = await receiver.ReceiveAsync(100, TimeSpan.FromSeconds(10));
                        if (messages == null)
                        {
                            break;
                        }

                        Log($"Received {messages.Count()} events from partition {receiver.PartitionId}");
                        lastEvents[receiver.PartitionId] = messages.Last();
                    }
                });

            await Task.WhenAll(receiveTasks);

            // Assert we have received at least one event from each partition.
            Assert.True(lastEvents.Count == PartitionIds.Count(), "SendAndReceiveSingleEvent didn't receive expected number of events");

            return lastEvents.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        async Task<Dictionary<string, List<EventData>>> RunGenericScenario(EventProcessorHost eventProcessorHost,
            EventProcessorOptions epo = null, int totalNumberOfEventsToSend = 1, bool checkPointLastEvent = true,
            bool checkPointBatch = false)
        {
            var receivedEvents = new ConcurrentDictionary<string, List<EventData>>();
            var lastReceivedAt = DateTime.Now;

            if (epo == null)
            {
                epo = new EventProcessorOptions { ReceiveTimeout = TimeSpan.FromSeconds(15) };
            }

            try
            {
                Log($"Calling RegisterEventProcessorAsync");
                var processorFactory = new TestEventProcessorFactory();

                processorFactory.OnCreateProcessor += (f, createArgs) =>
                {
                    var processor = createArgs.Item2;
                    string partitionId = createArgs.Item1.PartitionId;
                    string hostName = createArgs.Item1.Owner;
                    processor.OnOpen += (_, partitionContext) => Log($"{hostName} > Partition {partitionId} TestEventProcessor opened");
                    processor.OnClose += (_, closeArgs) => Log($"{hostName} > Partition {partitionId} TestEventProcessor closing: {closeArgs.Item2}");
                    processor.OnProcessError += (_, errorArgs) => Log($"{hostName} > Partition {partitionId} TestEventProcessor process error {errorArgs.Item2.Message}");
                    processor.OnProcessEvents += (_, eventsArgs) =>
                    {
                        int eventCount = eventsArgs.Item2 != null ? eventsArgs.Item2.events.Count() : 0;
                        Log($"{hostName} > Partition {partitionId} TestEventProcessor processing {eventCount} event(s)");
                        if (eventCount > 0)
                        {
                            List<EventData> events;
                            receivedEvents.TryGetValue(partitionId, out events);
                            if (events == null)
                            {
                                events = new List<EventData>();
                            }

                            events.AddRange(eventsArgs.Item2.events);
                            receivedEvents[partitionId] = events;
                            lastReceivedAt = DateTime.Now;
                        }

                        eventsArgs.Item2.checkPointLastEvent = checkPointLastEvent;
                        eventsArgs.Item2.checkPointBatch = checkPointBatch;
                    };
                };

                await eventProcessorHost.RegisterEventProcessorFactoryAsync(processorFactory, epo);

                Log($"Sending {totalNumberOfEventsToSend} event(s) to each partition");
                var sendTasks = new List<Task>();
                foreach (var partitionId in PartitionIds)
                {
                    for (int i = 0; i < totalNumberOfEventsToSend; i++)
                    {
                        sendTasks.Add(this.SendToPartitionAsync(partitionId, $"{partitionId} event.", this.ConnectionStringBuilder.ToString()));
                    }
                }

                await Task.WhenAll(sendTasks);

                // Wait until all partitions are silent, i.e. no more events to receive.
                while (lastReceivedAt > DateTime.Now.AddSeconds(-30))
                {
                    await Task.Delay(1000);
                }

                Log("Verifying at least an event was received by each partition");
                foreach (var partitionId in PartitionIds)
                {
                    Assert.True(receivedEvents.ContainsKey(partitionId), $"Partition {partitionId} didn't receive any message!");
                }

                Log("Success");
            }
            finally
            {
                Log("Calling UnregisterEventProcessorAsync");
                await eventProcessorHost.UnregisterEventProcessorAsync();
            }

            return receivedEvents.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        async Task<string[]> GetPartitionIdsAsync(string connectionString)
        {
            var eventHubClient = EventHubClient.CreateFromConnectionString(connectionString);
            try
            {
                var eventHubInfo = await eventHubClient.GetRuntimeInformationAsync();
                return eventHubInfo.PartitionIds;
            }
            finally
            {
                await eventHubClient.CloseAsync();
            }
        }

        async Task SendToPartitionAsync(string partitionId, string messageBody, string connectionString)
        {
            var eventHubClient = EventHubClient.CreateFromConnectionString(connectionString);
            try
            {
                var partitionSender = eventHubClient.CreatePartitionSender(partitionId);
                await partitionSender.SendAsync(new EventData(Encoding.UTF8.GetBytes(messageBody)));
            }
            finally
            {
                await eventHubClient.CloseAsync();
            }
        }

        void Log(string message)
        {
            var log = string.Format("{0} {1}", DateTime.Now.TimeOfDay, message);
            output.WriteLine(log);
            Debug.WriteLine(message);
            Console.WriteLine(message);

        }

        class TestEventProcessor : IEventProcessor
        {
            public event EventHandler<PartitionContext> OnOpen;
            public event EventHandler<Tuple<PartitionContext, CloseReason>> OnClose;
            public event EventHandler<Tuple<PartitionContext, ReceivedEventArgs>> OnProcessEvents;
            public event EventHandler<Tuple<PartitionContext, Exception>> OnProcessError;

            public TestEventProcessor()
            {
            }

            Task IEventProcessor.CloseAsync(PartitionContext context, CloseReason reason)
            {
                this.OnClose?.Invoke(this, new Tuple<PartitionContext, CloseReason>(context, reason));
                return Task.CompletedTask;
            }

            Task IEventProcessor.ProcessErrorAsync(PartitionContext context, Exception error)
            {
                this.OnProcessError?.Invoke(this, new Tuple<PartitionContext, Exception>(context, error));
                return Task.CompletedTask;
            }

            Task IEventProcessor.ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> events)
            {
                var eventsArgs = new ReceivedEventArgs();
                eventsArgs.events = events;
                this.OnProcessEvents?.Invoke(this, new Tuple<PartitionContext, ReceivedEventArgs>(context, eventsArgs));
                EventData lastEvent = events?.LastOrDefault();

                // Checkpoint with last event?
                if (eventsArgs.checkPointLastEvent && lastEvent != null)
                {
                    return context.CheckpointAsync(lastEvent);
                }

                // Checkpoint batch? This should checkpoint with last message delivered.
                if (eventsArgs.checkPointBatch)
                {
                    return context.CheckpointAsync();
                }

                return Task.CompletedTask;
            }

            Task IEventProcessor.OpenAsync(PartitionContext context)
            {
                this.OnOpen?.Invoke(this, context);
                return Task.CompletedTask;
            }
        }

        class TestEventProcessorFactory : IEventProcessorFactory
        {
            public event EventHandler<Tuple<PartitionContext, TestEventProcessor>> OnCreateProcessor;

            IEventProcessor IEventProcessorFactory.CreateEventProcessor(PartitionContext context)
            {
                var processor = new TestEventProcessor();
                this.OnCreateProcessor?.Invoke(this, new Tuple<PartitionContext, TestEventProcessor>(context, processor));
                return processor;
            }
        }

        class ReceivedEventArgs
        {
            public IEnumerable<EventData> events;
            public bool checkPointLastEvent = true;
            public bool checkPointBatch = false;
        }
    }
}
