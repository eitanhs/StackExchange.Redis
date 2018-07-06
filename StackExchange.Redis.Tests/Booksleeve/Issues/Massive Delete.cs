﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Redis.Tests.Booksleeve.Issues
{
    public class Massive_Delete : BookSleeveTestBase
    {
        public Massive_Delete(ITestOutputHelper output) : base(output) { }

        private void Prep(int db, string key)
        {
            var prefix = Me();
            using (var muxer = GetUnsecuredConnection(allowAdmin: true))
            {
                GetServer(muxer).FlushDatabase(db);
                Task last = null;
                var conn = muxer.GetDatabase(db);
                for (int i = 0; i < 10000; i++)
                {
                    string iKey = prefix + i;
                    conn.StringSetAsync(iKey, iKey);
                    last = conn.SetAddAsync(key, iKey);
                }
                conn.Wait(last);
            }
        }

        [FactLongRunning]
        public async Task ExecuteMassiveDelete()
        {
            const int db = 4;
            var key = Me();
            Prep(db, key);
            var watch = Stopwatch.StartNew();
            using (var muxer = GetUnsecuredConnection())
            using (var throttle = new SemaphoreSlim(1))
            {
                var conn = muxer.GetDatabase(db);
                var originally = await conn.SetLengthAsync(key).ForAwait();
                int keepChecking = 1;
                Task last = null;
                while (Volatile.Read(ref keepChecking) == 1)
                {
                    throttle.Wait(); // acquire
                    var x = conn.SetPopAsync(key).ContinueWith(task =>
                    {
                        throttle.Release();
                        if (task.IsCompleted)
                        {
                            if ((string)task.Result == null)
                            {
                                Volatile.Write(ref keepChecking, 0);
                            }
                            else
                            {
                                last = conn.KeyDeleteAsync((string)task.Result);
                            }
                        }
                    });
                    GC.KeepAlive(x);
                }
                if (last != null)
                {
                    await last;
                }
                watch.Stop();
                long remaining = await conn.SetLengthAsync(key).ForAwait();
                Output.WriteLine("From {0} to {1}; {2}ms", originally, remaining,
                    watch.ElapsedMilliseconds);

                var counters = GetServer(muxer).GetCounters();
                Output.WriteLine("Completions: {0} sync, {1} async", counters.Interactive.CompletedSynchronously, counters.Interactive.CompletedAsynchronously);
            }
        }
    }
}
