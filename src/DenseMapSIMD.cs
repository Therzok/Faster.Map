﻿using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Faster.Map.Core;

namespace Faster.Map
{
    /// <summary>
    /// This hashmap uses the following
    /// - open-addressing
    /// - Quadratic probing 
    /// - Loadfactor by default is 0.9 while maintaining an incredible speed
    /// - Fibonacci hashing
    /// - Searches in parallel using SIMD
    /// - First-come-first-serve collision resolution    
    /// - Tombstones to avoid backshifts
    /// </summary>
    public class DenseMapSIMD<TKey, TValue>
    {
        #region Properties

        /// <summary>
        /// Gets or sets how many elements are stored in the map
        /// </summary>
        /// <value>
        /// The entry count.
        /// </value>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the size of the map
        /// </summary>
        /// <value>
        /// The size.
        /// </value>
        public uint Size => (uint)_entries.Length;

        /// <summary>
        /// Returns all the entries as KeyValuePair objects
        /// </summary>
        /// <value>
        /// The entries.
        /// </value>
        public IEnumerable<KeyValuePair<TKey, TValue>> Entries
        {
            get
            {
                //iterate backwards so we can remove the item
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] >= 0)
                    {
                        var entry = _entries[i];
                        yield return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Returns all keys
        /// </summary>
        /// <value>
        /// The keys.
        /// </value>
        public IEnumerable<TKey> Keys
        {
            get
            {
                //iterate backwards so we can remove the jumpDistanceIndex item
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] >= 0)
                    {
                        yield return _entries[i].Key;
                    }
                }
            }
        }

        /// <summary>
        /// Returns all Values
        /// </summary>
        /// <value>
        /// The keys.
        /// </value>
        public IEnumerable<TValue> Values
        {
            get
            {
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] >= 0)
                    {
                        yield return _entries[i].Value;
                    }
                }
            }
        }

        #endregion

        #region Fields
        private const sbyte _emptyBucket = -127;
        private const sbyte _tombstone = -126;

        private static readonly Vector128<sbyte> _emptyBucketVector = Vector128.Create(_emptyBucket);

        private sbyte[] _metadata;
        private Entry<TKey, TValue>[] _entries;

        private const uint GoldenRatio = 0x9E3779B9; //2654435769;
        private uint _length;

        private int _shift = 32;
        private double _maxLookupsBeforeResize;
        private readonly double _loadFactor;
        private readonly IEqualityComparer<TKey> _comparer;
        private const sbyte _bitmask = (1 << 7) - 1;


        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        public DenseMapSIMD() : this(16, 0.90, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        public DenseMapSIMD(uint length) : this(length, 0.90, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        /// <param name="loadFactor">The loadfactor determines when the hashmap will resize(default is 0.9d)</param>
        public DenseMapSIMD(uint length, double loadFactor) : this(length, loadFactor, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        /// <param name="loadFactor">The loadfactor determines when the hashmap will resize(default is 0.9d)</param>
        /// <param name="keyComparer">Used to compare keys to resolve hashcollisions</param>
        public DenseMapSIMD(uint length, double loadFactor, IEqualityComparer<TKey> keyComparer)
        {
            if (!Vector128.IsHardwareAccelerated)
            {
                throw new NotSupportedException("Your hardware does not support acceleration for 128 bit vectors");
            }

            //default length is 16
            _length = length;
            _loadFactor = loadFactor;

            if (loadFactor > 0.9)
            {
                _loadFactor = 0.9;
            }

            if (_length < 16)
            {
                _length = 16;
            }

            if (BitOperations.IsPow2(length))
            {
                _length = length;
            }
            else
            {
                _length = BitOperations.RoundUpToPowerOf2(_length);
            }

            _maxLookupsBeforeResize = (uint)(_length * _loadFactor);
            _comparer = keyComparer ?? EqualityComparer<TKey>.Default;

            _shift = _shift - BitOperations.Log2(_length);
            _entries = new Entry<TKey, TValue>[_length + 16];
            _metadata = new sbyte[_length + 16];

            //fill metadata with emptybucket info
            Array.Fill(_metadata, _emptyBucket);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 
        /// Inserts a key and value in the hashmap
        ///
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>returns false if key already exists</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Emplace(TKey key, TValue value)
        {
            //Resize if loadfactor is reached
            if (Count > _maxLookupsBeforeResize)
            {
                Resize();
            }

            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Check if key is unique
                while (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);

                    uint indexAndOffset = index + Unsafe.As<int, uint>(ref offset);

                    var entry = GetArrayEntryByVal(_entries, indexAndOffset);

                    if (_comparer.Equals(entry.Key, key))
                    {
                        //duplicate key found
                        return false;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                result = right.ExtractMostSignificantBits();
                //check for tombstones and empty entries 
                if (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);
                    //calculate proper index
                    index += Unsafe.As<int, uint>(ref offset);

                    //retrieve entry
                    ref var currentEntry = ref GetArrayEntryRef(_entries, index);

                    //set key and value
                    currentEntry.Key = key;
                    currentEntry.Value = value;

                    ref var metadata = ref GetArrayEntryRef(_metadata, index);

                    // add h2 to metadata
                    metadata = Unsafe.As<long, sbyte>(ref h2);

                    ++Count;
                    return true;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index > _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }
            } while (true);
        }

        /// <summary>
        /// 
        /// Tries to emplace a key-value pair into the map
        ///
        /// If the map already contains this key, update the existing KeyValuePair
        ///
        /// * Example *
        ///
        /// var map = new DenseMapSIMD<uint, uint>(16, 0.5);
        ///
        /// map.EmplaceOrUpdate(1, 50);
        /// map.EmplaceOrUpdate(1, 60);
        ///
        /// var result = map.Get(1, out var result)
        ///
        /// Assert.AreEqual(60U, result)
        /// 
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EmplaceOrUpdate(TKey key, TValue value)
        {
            //Resize if loadfactor is reached
            if (Count > _maxLookupsBeforeResize)
            {
                Resize();
            }

            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Check if key is unique
                while (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);

                    uint indexAndOffset = index + Unsafe.As<int, uint>(ref offset);

                    ref var entry = ref GetArrayEntryRef(_entries, indexAndOffset);

                    if (_comparer.Equals(entry.Key, key))
                    {
                        //Key found, update existing key
                        entry.Value = value;
                        return;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                result = right.ExtractMostSignificantBits();
                //check for tombstones and empty entries 
                if (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);
                    //calculate proper index
                    index += Unsafe.As<int, uint>(ref offset);

                    //retrieve entry
                    ref var currentEntry = ref GetArrayEntryRef(_entries, index);

                    //set key and value
                    currentEntry.Key = key;
                    currentEntry.Value = value;

                    ref var metadata = ref GetArrayEntryRef(_metadata, index);

                    // add h2 to metadata
                    metadata = Unsafe.As<long, sbyte>(ref h2);

                    ++Count;
                    return;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index > _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }
            } while (true);
        }

        /// <summary>
        /// Tries to find the key in the map
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>Returns false if the key is not found</returns>       
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(TKey key, out TValue value)
        {
            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //Retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //Get index and eq
                    ref var entry = ref GetArrayEntryRef(_entries, index + Unsafe.As<int, uint>(ref offset));

                    //Use EqualityComparer to find proper entry
                    if (_comparer.Equals(entry.Key, key))
                    {
                        value = entry.Value;
                        return true;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                //Contains empty buckets;    
                if (Vector128.Equals(_emptyBucketVector, right).ExtractMostSignificantBits() != 0)
                {
                    value = default;
                    return false;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index >= _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //                                    
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot to back out;
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }

            } while (true);
        }
        
        /// <summary>
        /// Gets the value for the specified key, or, if the key is not present,
        /// adds an entry and returns the value by ref. This makes it possible to
        /// add or update a value in a single look up operation.
        ///
        /// Will only use one lookup instead of two
        ///
        /// * Example *
        ///
        /// var counterMap = new DenseMapSIMD<uint, uint>(16, 0.5);
        /// ref var counter = ref counterMap.GetOrAddValueRef(1);
        ///
        /// ++counter;
        /// 
        /// </summary>
        /// <param name="key">Key to look for</param>
        /// <returns>Reference to the new or existing value</returns>    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAddValueRef(TKey key)
        {
            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //Retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //Get index and eq
                    ref var entry = ref GetArrayEntryRef(_entries, index + Unsafe.As<int, uint>(ref offset));

                    //Use EqualityComparer to find proper entry
                    if (_comparer.Equals(entry.Key, key))
                    {
                        return ref entry.Value;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                //Empty entry, add key
                result = Vector128.Equals(_emptyBucketVector, right).ExtractMostSignificantBits();
                if (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);
                    //calculate proper index
                    index += Unsafe.As<int, uint>(ref offset);

                    //retrieve entry
                    ref var currentEntry = ref GetArrayEntryRef(_entries, index);

                    //set key and value
                    currentEntry.Key = key;
                    currentEntry.Value = default;

                    ref var metadata = ref GetArrayEntryRef(_metadata, index);

                    // add h2 to metadata
                    metadata = Unsafe.As<long, sbyte>(ref h2);

                    ++Count;
                    
                    return ref currentEntry.Value;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index >= _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //                                    
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot to back out;
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }

            } while (true);
        }
        
        /// <summary>
        /// Tries to find the key in the map and updates the value
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns> returns if update succeeded or not</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Update(TKey key, TValue value)
        {
            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    ref var entry = ref _entries[index + offset];

                    if (_comparer.Equals(entry.Key, key))
                    {
                        entry.Value = value;
                        return true;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                //get a bit sequence for matched empty buckets                
                if (Vector128.Equals(_emptyBucketVector, right).ExtractMostSignificantBits() != 0)
                {
                    //contains empty buckets - break;
                    return false;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index >= _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot to back out;
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }
            }
            while (true);
        }

        /// <summary>
        /// Removes a key and value from the map
        /// </summary>
        /// <param name="key"></param>
        /// <returns> returns if the removal succeeded </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(TKey key)
        {
            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    uint indexAndOffset = index + Unsafe.As<int, uint>(ref offset);

                    if (_comparer.Equals(_entries[indexAndOffset].Key, key))
                    {
                        _metadata[indexAndOffset] = _tombstone;
                        --Count;
                        return true;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                //find an empty spot, which means the key is not found             
                if (Vector128.Equals(_emptyBucketVector, right).ExtractMostSignificantBits() != 0)
                {
                    //contains empty buckets - break;
                    return false;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index >= _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    // 
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot to back out;
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }

            } while (true);
        }

        /// <summary>
        /// determines if hashmap contains key x
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns> returns if a key is found </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(TKey key)
        {
            // Get object identity hashcode
            var hashcode = (uint)key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            // Get 7 high bits
            var h2 = hashcode & _bitmask;

            //Create vector of the 7 high bits
            var left = Vector128.Create(Unsafe.As<long, sbyte>(ref h2));

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index));

                //get a bit sequence for matched hashcodes (h2s)
                var result = Vector128.Equals(left, right).ExtractMostSignificantBits();

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    var entry = _entries[index + offset];

                    if (_comparer.Equals(entry.Key, key))
                    {
                        return true;
                    }

                    //clear bit
                    result &= ~(1u << offset);
                }

                if (Vector128.Equals(_emptyBucketVector, right).ExtractMostSignificantBits() != 0)
                {
                    //contains empty buckets - break;  
                    return false;
                }

                //Probing is done by incrementing the currentEntry bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
                //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
                //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
                //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS).
                //Also note that we expect to almost never actually probe, since that’s WIDTH(16) non-EMPTY buckets we need to fail to find our key in.

                jumpDistance += 16;
                index += jumpDistance;

                if (index >= _length)
                {
                    // hashing to the top region of this hashmap always had some drawbacks
                    // even when the table was half full the table would resize when the last 16 slots were full
                    // and the jumpdistance exceeded the length of the array. this is not intended
                    // 
                    // when the index exceeds the length, which means all groups of 16 near the upper region of the map are full
                    // reset the index and try probing again from the start this will enforce a secure and trustable hashmap which will always
                    // resize when we reach a 90% load
                    // Note these entries will not be properly cache alligned but in the end its well worth it
                    //
                    // adding jumpdistance to the index will prevent endless loops.
                    // Every time this code block is entered jumpdistance will be different hence the index will be different too
                    // thus it will always look for an empty spot to back out;
                    index = Fmix(hashcode + jumpDistance) >> _shift;
                }
            } while (true);
        }

        /// <summary>
        /// Copies entries from one map to another
        /// </summary>
        /// <param name="denseMap">The map.</param>
        public void Copy(DenseMapSIMD<TKey, TValue> denseMap)
        {
            for (var i = 0; i < denseMap._entries.Length; ++i)
            {
                if (denseMap._metadata[i] < 0)
                {
                    continue;
                }

                var entry = denseMap._entries[i];
                Emplace(entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// Removes all entries from this map and sets the count to 0
        /// </summary>
        public void Clear()
        {
            Array.Clear(_entries);
            Array.Fill(_metadata, _emptyBucket);

            Count = 0;
        }

        /// <summary>
        /// Gets or sets the value by using a Tkey
        /// </summary>
        /// <value>
        /// The 
        /// </value>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException">
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// or
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// </exception>
        public TValue this[TKey key]
        {
            get
            {
                if (Get(key, out var result))
                {
                    return result;
                }

                throw new KeyNotFoundException($"Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}");
            }
            set
            {
                if (!Update(key, value))
                {
                    throw new KeyNotFoundException($"Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}");
                }
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Emplaces a new entry without checking for key existence. Keys have already been checked and are unique
        /// </summary>
        /// <param name="entry">The entry.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmplaceInternal(Entry<TKey, TValue> entry, sbyte h2)
        {
            //expensive if hashcode is slow, or when it`s not cached like strings
            var hashcode = (uint)entry.Key.GetHashCode();

            //calculate index by using object identity * fibonaci followed by a shift
            uint index = hashcode * GoldenRatio >> _shift;

            //Set initial jumpdistance index
            uint jumpDistance = 0;

            do
            {
                //check for empty entries
                var result = Vector128.LoadUnsafe(ref GetArrayEntryRef(_metadata, index)).ExtractMostSignificantBits();
                if (result != 0)
                {
                    var offset = BitOperations.TrailingZeroCount(result);

                    index += Unsafe.As<int, uint>(ref offset);

                    _metadata[index] = h2;
                    _entries[index] = entry;
                    return;
                }

                //Calculate jumpDistance
                jumpDistance += 16;
                index += jumpDistance;

            } while (true);
        }

        /// <summary>
        /// Resizes this instance.
        /// </summary>     
        private void Resize()
        {
            _shift--;

            //next power of 2
            _length = _length * 2;
            _maxLookupsBeforeResize = _length * _loadFactor;

            var oldEntries = _entries;
            var oldMetadata = _metadata;

            var size = Unsafe.As<uint, int>(ref _length) + 16;

            _metadata = GC.AllocateUninitializedArray<sbyte>(size);
            _entries = GC.AllocateUninitializedArray<Entry<TKey, TValue>>(size);

            _metadata.AsSpan().Fill(_emptyBucket);

            for (var i = 0; i < oldEntries.Length; ++i)
            {
                var m = oldMetadata[i];
                if (m < 0)
                {
                    continue;
                }

                EmplaceInternal(oldEntries[i], m);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T GetArrayEntryByVal<T>(T[] array, uint index)
        {
#if DEBUG
            return array[index];
#else
            ref var arr0 = ref MemoryMarshal.GetArrayDataReference(array);
            return Unsafe.Add(ref arr0, index);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref T GetArrayEntryRef<T>(T[] array, uint index)
        {
#if DEBUG
            return ref array[index];
#else
            ref var arr0 = ref MemoryMarshal.GetArrayDataReference(array);
            return ref Unsafe.Add(ref arr0, index);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Fmix(uint h)
        {
            // pipelining friendly algorithm
            h = (h ^ (h >> 16)) * 0x85ebca6b;
            h = (h ^ (h >> 13)) * 0xc2b2ae35;
            return h ^ (h >> 16);
        }
        #endregion
    }
}