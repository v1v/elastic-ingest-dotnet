// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Linq;
using System.Threading;
using Elastic.Channels;
using Elastic.Ingest.Elasticsearch.Indices;
using Elastic.Transport;
using Elastic.Transport.VirtualizedCluster;
using Elastic.Transport.VirtualizedCluster.Components;
using Elastic.Transport.VirtualizedCluster.Rules;

namespace Elastic.Ingest.Elasticsearch.Tests
{
	public static class TestSetup
	{
		public static HttpTransport<TransportConfiguration> CreateClient(Func<VirtualCluster, VirtualCluster> setup)
		{
			var cluster = Virtual.Elasticsearch.Bootstrap(numberOfNodes: 1).Ping(c=>c.SucceedAlways());
			var virtualSettings = setup(cluster)
				.StaticNodePool()
				.Settings(s=>s.DisablePing());

			//var audit = new Auditor(() => virtualSettings);
			//audit.VisualizeCalls(cluster.ClientCallRules.Count);

			var settings = new TransportConfiguration(virtualSettings.ConnectionPool, virtualSettings.Connection)
				.DisablePing()
				.EnableDebugMode();
			return new DefaultHttpTransport<TransportConfiguration>(settings);
		}

		public static ClientCallRule BulkResponse(this ClientCallRule rule, params int[] statusCodes) =>
			rule.Succeeds(TimesHelper.Once).ReturnResponse(BulkResponseBuilder.CreateResponse(statusCodes));

		public class TestSession : IDisposable
		{
			private int _rejections;
			private int _requests;
			private int _responses;
			private int _retries;
			private int _maxRetriesExceeded;

			public TestSession(HttpTransport<TransportConfiguration> transport)
			{
				Transport = transport;
				BufferOptions = new BufferOptions
				{
					ConcurrentConsumers = 1,
					MaxConsumerBufferSize = 2,
					MaxConsumerBufferLifetime = TimeSpan.FromSeconds(10),
					WaitHandle = WaitHandle,
					MaxRetries = 3,
					BackoffPeriod = _ => TimeSpan.FromMilliseconds(1),
				};
				ChannelOptions = new IndexChannelOptions<TestDocument>(transport)
				{
					BufferOptions = BufferOptions,
					ServerRejectionCallback = (_) => Interlocked.Increment(ref _rejections),
					BulkAttemptCallback = (_, _) => Interlocked.Increment(ref _requests),
					ResponseCallback = (_, _) => Interlocked.Increment(ref _responses),
					MaxRetriesExceededCallback = (_) => Interlocked.Increment(ref _maxRetriesExceeded),
					RetryCallBack = (_) => Interlocked.Increment(ref _retries),
					ExceptionCallback= (e) => LastException = e
				};
				Channel = new IndexChannel<TestDocument>(ChannelOptions);
			}

			public IndexChannel<TestDocument> Channel { get; }

			public HttpTransport<TransportConfiguration> Transport { get; }

			public IndexChannelOptions<TestDocument> ChannelOptions { get; }

			public BufferOptions BufferOptions { get; }

			public CountdownEvent WaitHandle { get; } = new CountdownEvent(1);

			public int Rejections => _rejections;
			public int TotalBulkRequests => _requests;
			public int TotalBulkResponses => _responses;
			public int TotalRetries => _retries;
			public int MaxRetriesExceeded => _maxRetriesExceeded;
			public Exception LastException { get; private set; }

			public void Wait()
			{
				WaitHandle.Wait(TimeSpan.FromSeconds(10));
				WaitHandle.Reset();
			}

			public void Dispose()
			{
				Channel?.Dispose();
				WaitHandle?.Dispose();
			}
		}

		public static TestSession CreateTestSession(HttpTransport<TransportConfiguration> transport) =>
			new TestSession(transport);

		public static void WriteAndWait(this TestSession session, int events = 1)
		{
			foreach (var _ in Enumerable.Range(0, events))
				session.Channel.TryWrite(new TestDocument { Timestamp = DateTimeOffset.UtcNow });
			session.Wait();
		}
	}
}