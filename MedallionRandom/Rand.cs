﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Medallion
{
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using Random = System.Random;
    
    /// <summary>
    /// Contains helpers and extensions for working with random number generators
    /// </summary>
    public static class Rand
    {
        #region ---- Java Extensions ----
        /// <summary>
        /// Returns a random boolean value
        /// </summary>
        public static bool NextBoolean(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return random.NextBits(1) != 0;
        }

        /// <summary>
        /// Returns a random 32-bit integer
        /// </summary>
        public static int NextInt32(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return random.NextBits(32);
        }

        /// <summary>
        /// Returns a random 64-bit integer
        /// </summary>
        public static long NextInt64(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            var nextBitsRandom = random as NextBitsRandom;
            if (nextBitsRandom != null)
            {
                return ((long)nextBitsRandom.NextBits(32) << 32) + nextBitsRandom.NextBits(32);
            }

            // NextBits(32) for regular Random requires 2 calls to Next(), or 4 calls
            // total using the method above. Thus, we instead use an approach that requires
            // only 3 calls
            return ((long)random.Next30OrFewerBits(22) << 42)
                + ((long)random.Next30OrFewerBits(21) << 21)
                + random.Next30OrFewerBits(21);
        }

        /// <summary>
        /// Returns a random <see cref="float"/> value in [0, 1)
        /// </summary>
        public static float NextSingle(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return random.NextBits(24) / ((float)(1 << 24));
        }

        /// <summary>
        /// Returns the sequence of values that would be generated by repeated
        /// calls to <see cref="Random.NextDouble"/>
        /// </summary>
        public static IEnumerable<double> NextDoubles(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return NextDoublesIterator(random);
        }

        private static IEnumerable<double> NextDoublesIterator(Random random)
        {
            while (true)
            {
                yield return random.NextDouble();
            }
        }

        private static int NextBits(this Random random, int bits)
        {
            var nextBitsRandom = random as NextBitsRandom;
            if (nextBitsRandom != null)
            {
                return nextBitsRandom.NextBits(bits);
            }

            // simulate with native random methods. 32 bits requires [int.MinValue, int.MaxValue]
            // and 31 bits requires [0, int.MaxValue]

            // 30 or fewer bits needs only one call
            if (bits <= 30)
            {
                return random.Next30OrFewerBits(bits);
            }
            
            var upperBits = random.Next30OrFewerBits(bits - 16) << 16;
            var lowerBits = random.Next30OrFewerBits(16);
            return upperBits + lowerBits;
        }
        
        private static int Next30OrFewerBits(this Random random, int bits)
        {
            // a range of bits is [0, 2^bits - 1)
            var maxValue = (1 << bits) - 1;

            int sample, val;
            do
            {
                // take a sample [0, 2^31 - 2)
                sample = random.Next();
                // derive a value in [0, maxValue)
                val = sample % (maxValue + 1);
            }
            // rejects biased values. For example, if Next() returned [0, 10)
            // and we were looking for a number in [0, 4), we'd reject samples of
            // 8 or 9 to ensure that each number in the desired range has an even chance
            // of turning up
            while (sample - val + (maxValue - 1) < 0);

            return val;
        }
        #endregion

        #region ---- Weighted Coin Flip ----
        /// <summary>
        /// Returns true with probability <paramref name="probability"/>
        /// </summary>
        public static bool NextBoolean(this Random random, double probability)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            if (probability == 0) { return false; }
            if (probability == 1) { return true; }
            if (probability < 0 || probability > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(probability), $"{nameof(probability)} must be in [0, 1]. Found {probability}");
            }

            return random.NextDouble() < probability;
        }
        #endregion

        #region ---- Byte Stream ----
        /// <summary>
        /// Returns the sequence of bytes that would be returned by repeated calls
        /// to <see cref="Random.NextBytes(byte[])"/>
        /// </summary>
        public static IEnumerable<byte> NextBytes(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return NextBytesIterator(random);
        }

        private static IEnumerable<byte> NextBytesIterator(Random random)
        {
            var buffer = new byte[256];
            while (true)
            {
                random.NextBytes(buffer);
                for (var i = 0; i < buffer.Length; ++i)
                {
                    yield return buffer[i];
                }
            }
        }
        #endregion

        #region ---- Shuffling ----
        /// <summary>
        /// Returns <paramref name="source"/> randomly shuffled using 
        /// <paramref name="random"/> or else <see cref="Rand.Current"/>
        /// </summary>
        public static IEnumerable<T> Shuffled<T>(this IEnumerable<T> source, Random random = null)
        {
            return ShuffledIterator(source, random);
        }

        private static IEnumerable<T> ShuffledIterator<T>(IEnumerable<T> source, Random random)
        {
            var list = source.ToList();
            if (list.Count == 0)
            {
                yield break;
            }

            // note that it is vital that we use SingletonRandom.Instance over ThreadLocalRandom.Instance here,
            // since the iterator could be advanced on different threads thus violating thread-safety
            var rand = random ?? SingletonRandom.Instance;
            for (var i = 0; i < list.Count - 1; ++i)
            {
                // swap i with a random index and yield the swapped value
                var randomIndex = rand.Next(minValue: i, maxValue: list.Count);
                var randomValue = list[randomIndex];
                list[randomIndex] = list[i];
                // note that we don't even have to put randomValue in list[i], because this is a throwaway list!
                yield return randomValue;
            }

            // yield the last value
            yield return list[list.Count - 1];
        }

        /// <summary>
        /// Shuffles the given <paramref name="list"/> using <paramref name="random"/> 
        /// if provided or else <see cref="Rand.Current"/>
        /// </summary>
        public static void Shuffle<T>(this IList<T> list, Random random = null)
        {
            if (list == null) { throw new ArgumentNullException(nameof(list)); }

            var rand = random ?? ThreadLocalRandom.Current;

            for (var i = 0; i < list.Count - 1; ++i)
            {
                // swap i with a random index
                var randomIndex = rand.Next(minValue: i, maxValue: list.Count);
                var randomValue = list[randomIndex];
                list[randomIndex] = list[i];
                list[i] = randomValue;
            }
        }
        #endregion

        #region ---- Gaussian ----
        /// <summary>
        /// Returns a normally-distributed double value with mean 0 and standard deviation 1
        /// </summary>
        public static double NextGaussian(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            var nextGaussianRandom = random as INextGaussianRandom;
            if (nextGaussianRandom != null)
            {
                return nextGaussianRandom.NextGaussian();
            }

            double result, ignored;
            random.NextTwoGaussians(out result, out ignored);
            return result;
        }

        /// <summary>
        /// Returns a sequence of normally-distributed double values with mean 0 and standard
        /// deviation 1.
        /// </summary>
        public static IEnumerable<double> NextGaussians(this Random random)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }

            return NextGaussiansIterator(random);
        }

        private static IEnumerable<double> NextGaussiansIterator(Random random)
        {
            var nextGaussiansRandom = random as INextGaussianRandom;
            if (nextGaussiansRandom != null)
            {
                while (true) { yield return nextGaussiansRandom.NextGaussian(); }
            }

            while (true)
            {
                double next, nextNext;
                random.NextTwoGaussians(out next, out nextNext);
                yield return next;
                yield return nextNext;
            }
        }

        private interface INextGaussianRandom
        {
            double NextGaussian();
        }

        private static void NextTwoGaussians(this Random random, out double value1, out double value2)
        {
            double v1, v2, s;
            do
            {
                v1 = 2 * random.NextDouble() - 1; // between -1.0 and 1.0
                v2 = 2 * random.NextDouble() - 1; // between -1.0 and 1.0
                s = v1 * v1 + v2 * v2;
            } while (s >= 1 || s == 0);
            double multiplier = Math.Sqrt(-2 * Math.Log(s) / s);

            value1 = v1* multiplier;
            value2 = v2 * multiplier; 
        }
        #endregion

        #region ---- Bounded Doubles ----
        /// <summary>
        /// Returns a random double value uniformly in [0, <paramref name="max"/>). The underlying randomness is
        /// provided by <see cref="Random.NextDouble"/>, which may be unsuitable for very large ranges
        /// </summary>
        public static double NextDouble(this Random random, double max)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }
            if (max < 0) { throw new ArgumentOutOfRangeException(nameof(max), max, "must be non-negative"); }
            if (double.IsNaN(max) || double.IsInfinity(max)) { throw new ArgumentException("must not be infinity or NaN", nameof(max)); }

            if (max == 0) { return 0; } // consistent with Next(int)

            return max * random.NextDouble();
        }

        /// <summary>
        /// Returns a random double value uniformly in [<paramref name="min"/>, <paramref name="max"/>). The 
        /// underlying randomness is provided by <see cref="Random.NextDouble"/>, which may be unsuitable for 
        /// very large ranges
        /// </summary>
        public static double NextDouble(this Random random, double min, double max)
        {
            if (random == null) { throw new ArgumentNullException(nameof(random)); }
            var range = max - min;
            if (double.IsNaN(range) || double.IsInfinity(range))
            {
                // these are all checked inside a block for both conditions to handle things like
                // Inf - Inf = NaN

                if (double.IsNaN(min)) { throw new ArgumentException("must not be NaN", nameof(min)); };
                if (double.IsNaN(max)) { throw new ArgumentException("must not be NaN", nameof(max)); };
                if (double.IsInfinity(min)) { throw new ArgumentOutOfRangeException(nameof(min), min, "must not be infinite"); }
                if (double.IsInfinity(max)) { throw new ArgumentOutOfRangeException(nameof(max), max, "must not be infinite"); }
                throw new ArgumentOutOfRangeException(nameof(max), max, $"difference between {min} and {max} is too large to be represented by {typeof(double)}");
            }
            if (range < 0) { throw new ArgumentOutOfRangeException(nameof(max), max, "must be greater than or equal to " + nameof(min)); }

            if (range == 0) { return min; } // consistent with Next(int, int)

            return min + (range * random.NextDouble());
        }
        #endregion

        #region ---- ThreadLocal ----  
        /// <summary>
        /// Returns a thread-safe <see cref="Random"/> instance which can be used 
        /// for static random calls
        /// </summary>
        public static Random Current => SingletonRandom.Instance;

        /// <summary>
        /// Returns a double value in [0, 1)
        /// </summary>
        public static double NextDouble() => ThreadLocalRandom.Current.NextDouble();

        /// <summary>
        /// Returns an int value in [<paramref name="minValue"/>, <paramref name="maxValue"/>)
        /// </summary>
        public static int Next(int minValue, int maxValue) => ThreadLocalRandom.Current.Next(minValue, maxValue);

        private sealed class SingletonRandom : Random, INextGaussianRandom
        {
            public static readonly SingletonRandom Instance = new SingletonRandom();

            private SingletonRandom() : base(0) { }

            public override int Next() => ThreadLocalRandom.Current.Next();

            public override int Next(int maxValue) => ThreadLocalRandom.Current.Next(maxValue);

            public override int Next(int minValue, int maxValue) => ThreadLocalRandom.Current.Next(minValue, maxValue);

            public override void NextBytes(byte[] buffer) => ThreadLocalRandom.Current.NextBytes(buffer);

            public override double NextDouble() => ThreadLocalRandom.Current.NextDouble();

            protected override double Sample() => ThreadLocalRandom.Current.NextDouble();

            double INextGaussianRandom.NextGaussian() => ThreadLocalRandom.Current.NextGaussian();
        }

        private sealed class ThreadLocalRandom : Random, INextGaussianRandom
        {
            [ThreadStatic]
            private static ThreadLocalRandom currentInstance;

            public static ThreadLocalRandom Current { get { return currentInstance ?? (currentInstance = new ThreadLocalRandom()); } }

            private ThreadLocalRandom()
                : base(Seed: unchecked((31 * Thread.CurrentThread.ManagedThreadId) + Environment.TickCount))
            {
            }
            
            private double? nextNextGaussian;

            public double NextGaussian()
            {
                if (this.nextNextGaussian.HasValue)
                {
                    var result = this.nextNextGaussian.Value;
                    this.nextNextGaussian = null;
                    return result;
                }

                double next, nextNext;
                this.NextTwoGaussians(out next, out nextNext);
                this.nextNextGaussian = nextNext;
                return next;
            }
        }
        #endregion

        #region ---- Factory ---- 
        /// <summary>
        /// Comparable to <code>new Random()</code>, but seeds the <see cref="Random"/> with
        /// a time-dependent value that will still vary greatly across calls to <see cref="Create"/>.
        /// This avoids the problem of many <see cref="Random"/>s created close together being seeded
        /// with the same value
        /// </summary>
        public static Random Create()
        {
            var combinedSeed = unchecked((31 * Environment.TickCount) + ThreadLocalRandom.Current.Next());
            return new Random(combinedSeed);
        }
        #endregion

        #region ---- System.IO.Stream Interop ----
        /// <summary>
        /// Creates a <see cref="Random"/> instance which uses the bytes from <see cref="Stream"/>
        /// as a source of randomness
        /// </summary>
        public static Random FromStream(Stream randomBytes)
        {
            if (randomBytes == null) { throw new ArgumentNullException(nameof(randomBytes)); }
            if (!randomBytes.CanRead) { throw new ArgumentException("must be readable", nameof(randomBytes)); }

            return new StreamRandomNumberGenerator(randomBytes).AsRandom();
        }
        
        private sealed class StreamRandomNumberGenerator : RandomNumberGenerator
        {
            private readonly Stream stream;

            internal StreamRandomNumberGenerator(Stream stream)
            {
                this.stream = stream;
            }

            public override void GetBytes(byte[] data)
            {
                var justResetStream = false;
                var bytesRead = 0;
                while (bytesRead < data.Length)
                {
                    // based on StreamReader.ReadBlock. We don't want to
                    // give up until the end of the file is reached

                    var nextBytesRead = this.stream.Read(data, offset: bytesRead, count: data.Length - bytesRead);
                    if (nextBytesRead == 0) // eof
                    {
                        if (!this.stream.CanSeek)
                        {
                            throw new InvalidOperationException("Cannot produce additional random bytes because the given stream is exhausted and does not support seeking");
                        }
                        if (justResetStream)
                        {
                            // prevents us from going into an infinite loop seeking back to the beginning of an empty stream
                            throw new InvalidOperationException("Cannot produce additional random bytes because the given stream is empty");
                        }

                        // reset the stream
                        this.stream.Seek(0, SeekOrigin.Begin);
                        justResetStream = true;
                    }
                    else
                    {
                        bytesRead += nextBytesRead;
                        justResetStream = false;
                    }
                }
            }
        }
        #endregion
        
        #region ---- NextBits Random ----
        private abstract class NextBitsRandom : Random, INextGaussianRandom
        {
            // pass through the seed just in case
            protected NextBitsRandom(int seed) : base(seed) { }

            internal abstract int NextBits(int bits);

            #region ---- .NET Random Methods ----
            public sealed override int Next()
            {
                return this.Next(int.MaxValue);
            }

            public sealed override int Next(int maxValue)
            {
                // see remarks for this special case in the docs:
                // https://msdn.microsoft.com/en-us/library/zd1bc8e5%28v=vs.110%29.aspx?f=255&MSPPError=-2147217396
                if (maxValue == 0)
                {
                    return 0;
                }
                if (maxValue <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxValue), $"{nameof(maxValue)} must be positive.");
                }

                unchecked
                {
                    if ((maxValue & -maxValue) == maxValue)  // i.e., bound is a power of 2
                    {
                        return (int)((maxValue * (long)this.NextBits(31)) >> 31);
                    }

                    int bits, val;
                    do
                    {
                        bits = this.NextBits(31);
                        val = bits % maxValue;
                    } while (bits - val + (maxValue - 1) < 0);
                    return val;
                }
            }

            public sealed override int Next(int minValue, int maxValue)
            {
                if (minValue == maxValue)
                {
                    return minValue;
                }
                if (minValue > maxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(minValue), $"{nameof(minValue)} ({minValue}) must not be > {nameof(maxValue)} ({maxValue})");
                }

                var range = (long)maxValue - minValue;

                // if the range is small, we can use Next(int)
                if (range <= int.MaxValue)
                {
                    return minValue + this.Next(maxValue: (int)range);
                }

                // otherwise, we use java's implementation for 
                // nextLong(long, long)
                var r = this.NextInt64();
                var m = range - 1;

                // power of two
                if ((range & m) == 0L)
                {
                    r = (r & m);
                }
                else
                {
                    // reject over-represented candidates
                    for (
                        var u = unchecked((long)((ulong)r >> 1)); // ensure non-negative
                        u + m - (r = u % range) < 0; // rejection check
                        u = unchecked((long)((ulong)this.NextInt64() >> 1)) // retry
                    ) ; 
                }

                return checked((int)(r + minValue));
            }

            public override void NextBytes(byte[] buffer)
            {
                if (buffer == null) { throw new ArgumentNullException(nameof(buffer)); }

                for (int i = 0; i < buffer.Length;)
                {
                    for (int rand = this.NextBits(32), n = Math.Min(buffer.Length - i, 4);
                         n-- > 0; 
                         rand >>= 8)
                    {
                        buffer[i++] = unchecked((byte)rand);
                    }
                }
            }

            public sealed override double NextDouble()
            {
                return this.Sample();
            }

            protected sealed override double Sample()
            {
                return (((long)this.NextBits(26) << 27) + this.NextBits(27)) / (double)(1L << 53);
            }
            #endregion

            private double? nextNextGaussian;

            double INextGaussianRandom.NextGaussian()
            {
                if (this.nextNextGaussian.HasValue)
                {
                    var result = this.nextNextGaussian.Value;
                    this.nextNextGaussian = null;
                    return result;
                }

                double next, nextNext;
                this.NextTwoGaussians(out next, out nextNext);
                this.nextNextGaussian = nextNext;
                return next;
            }
        }
        #endregion

        #region ---- Java Random ----
        /// <summary>
        /// Creates a <see cref="Random"/> that uses the same algorithm as the JRE. The <see cref="Random"/>
        /// is seeded with a time-dependent value which will vary greatly even across close-together calls to
        /// <see cref="CreateJavaRandom()"/>
        /// </summary>
        public static Random CreateJavaRandom()
        {
            var seed = ((long)Environment.TickCount << 32) | (uint)ThreadLocalRandom.Current.Next();
            return CreateJavaRandom(seed);
        }

        /// <summary>
        /// Creates a <see cref="Random"/> which replicates the same random sequence as is produced by
        /// the standard random number generator in the JRE using the same <paramref name="seed"/>
        /// </summary>
        public static Random CreateJavaRandom(long seed)
        {
            return new JavaRandom(seed);
        }

        private sealed class JavaRandom : NextBitsRandom
        {
            private long seed;

            public JavaRandom(long seed)
                // we shouldn't need the seed, but passing it through
                // just in case new Random() methods are added in the future
                // that don't call anything we've overloaded
                : base(unchecked((int)seed))
            {
                // this is based on "initialScramble()" in the Java implementation
                this.seed = (seed ^ 0x5DEECE66DL) & ((1L << 48) - 1);
            }

            internal override int NextBits(int bits)
            {
                unchecked
                {
                    this.seed = ((seed * 0x5DEECE66DL) + 0xBL) & ((1L << 48) - 1);
                    return (int)((ulong)this.seed >> (48 - bits));
                }
            }
        }
        #endregion

        #region ---- RandomNumberGenerator Interop ----
        /// <summary>
        /// Returns a <see cref="Random"/> instance which uses the given <paramref name="randomNumberGenerator"/>
        /// as a source of randomness
        /// </summary>
        public static Random AsRandom(this RandomNumberGenerator randomNumberGenerator)
        {
            if (randomNumberGenerator == null) { throw new ArgumentNullException(nameof(randomNumberGenerator)); }

            return new RandomNumberGeneratorRandom(randomNumberGenerator);
        }
        
        private sealed class RandomNumberGeneratorRandom : NextBitsRandom
        {
            private const int BufferLength = 512;

            private readonly RandomNumberGenerator rand;
            private readonly byte[] buffer = new byte[BufferLength];
            private int nextByteIndex = BufferLength;

            internal RandomNumberGeneratorRandom(RandomNumberGenerator randomNumberGenerator)
                : base(seed: 0) // avoid having to generate a time-based seed 
            {
                this.rand = randomNumberGenerator;
            }
            
            internal override int NextBits(int bits)
            {
                // unsigned so we can unsigned shift below
                uint result = 0;
                checked
                {
                    for (var i = 0; i < bits; i += 8)
                    {
                        if (this.nextByteIndex == BufferLength)
                        {
                            this.rand.GetBytes(this.buffer);
                            this.nextByteIndex = 0;
                        }
                        result += (uint)this.buffer[this.nextByteIndex++] << i;
                    }
                }
                
                var nextBits = result >> (32 - bits); 
                return unchecked((int)nextBits);
            }

            // we override this for performance reasons, since we can call the underlying RNG's NextBytes() method directly
            public override void NextBytes(byte[] buffer)
            {
                if (buffer == null) { throw new ArgumentNullException(nameof(buffer)); }

                if (buffer.Length <= (BufferLength - this.nextByteIndex))
                {
                    for (var i = this.nextByteIndex; i < buffer.Length; ++i)
                    {
                        buffer[i] = this.buffer[i];
                    }
                    this.nextByteIndex += buffer.Length;
                }
                else
                {
                    this.rand.GetBytes(buffer);
                }
            }
        }
        #endregion
    }
}
