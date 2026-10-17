using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>
    /// Статистика производства: сколько предметов в минуту производится и потребляется
    /// (скользящее окно 60 секунд, корзины по секунде).
    /// </summary>
    public sealed class ProductionStats
    {
        private const int Buckets = 60;

        private sealed class Series
        {
            public readonly long[] Produced = new long[Buckets];
            public readonly long[] Consumed = new long[Buckets];
            public long TotalProduced;
            public long TotalConsumed;
        }

        private readonly Dictionary<int, Series> _series = new Dictionary<int, Series>();
        private int _bucket;
        private int _tickInBucket;

        public void Produced(int item, int count)
        {
            var s = Get(item);
            s.Produced[_bucket] += count;
            s.TotalProduced += count;
        }

        public void Consumed(int item, int count)
        {
            var s = Get(item);
            s.Consumed[_bucket] += count;
            s.TotalConsumed += count;
        }

        private Series Get(int item)
        {
            if (!_series.TryGetValue(item, out var s)) _series[item] = s = new Series();
            return s;
        }

        public void Tick()
        {
            if (++_tickInBucket < GameConstants.TicksPerSecond) return;
            _tickInBucket = 0;
            _bucket = (_bucket + 1) % Buckets;
            foreach (var s in _series.Values)
            {
                s.Produced[_bucket] = 0;
                s.Consumed[_bucket] = 0;
            }
        }

        public long ProducedPerMinute(int item)
        {
            if (!_series.TryGetValue(item, out var s)) return 0;
            long sum = 0;
            foreach (var v in s.Produced) sum += v;
            return sum;
        }

        public long ConsumedPerMinute(int item)
        {
            if (!_series.TryGetValue(item, out var s)) return 0;
            long sum = 0;
            foreach (var v in s.Consumed) sum += v;
            return sum;
        }

        public long TotalProduced(int item) => _series.TryGetValue(item, out var s) ? s.TotalProduced : 0;

        public IEnumerable<int> Items => _series.Keys;

        public void Write(BinaryWriter w, ContentDatabase content)
        {
            w.Write(_series.Count);
            foreach (var kv in _series)
            {
                w.Write(content.Items.IdOf(kv.Key) ?? "");
                w.Write(kv.Value.TotalProduced);
                w.Write(kv.Value.TotalConsumed);
            }
        }

        public void Read(BinaryReader r, ContentDatabase content)
        {
            _series.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                long p = r.ReadInt64();
                long c = r.ReadInt64();
                int idx = content.Items.IndexOf(id);
                if (idx < 0) continue;
                var s = Get(idx);
                s.TotalProduced = p;
                s.TotalConsumed = c;
            }
        }
    }
}
