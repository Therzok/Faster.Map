﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Faster.Map.Core.Tests
{
    [TestClass]
    public class DenseMapSIMDTests
    {
        private uint[] keys;

        [TestInitialize]
        public void Setup()
        {
            var output = File.ReadAllText("Numbers.txt");
            var splittedOutput = output.Split(',');

            keys = new uint[splittedOutput.Length];

            for (var index = 0; index < splittedOutput.Length; index++)
            {
                keys[index] = uint.Parse(splittedOutput[index]);
            }

            //     Shuffle(new Random(), keys);
        }

        [TestMethod]
        public void AssertDailyUseCaseWithoutResize()
        {
            var fmap = new DenseMapSIMD<uint, uint>(1000000);

            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Emplace(k, k))
                {
                    throw new InternalTestFailureException("Error occured while add");
                }
            }

            Assert.AreEqual(900000, fmap.Count);

            //find all entries from map

            //var offset = 0;
            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Get(k, out var result))
                {
                    throw new InternalTestFailureException("Error occured while get");
                }
            }

            //remove all entries from map

            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Remove(k))
                {
                    throw new InternalTestFailureException("Error occured while removing");
                }
            }

            Assert.IsTrue(fmap.Count == 0);

            //map full of tombstones, try inserting again

            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Emplace(k, k))
                {
                    throw new InternalTestFailureException("Error occured while removing");
                }
            }

            Assert.AreEqual(900000, fmap.Count);
        }

        [TestMethod]
        public void AssertUpdateEntryInMapReturnsProperValue()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);
            map.Emplace(1, 1);

            //act
            map.Update(1, 100);

            //assert
            map.Get(1, out var result);

            Assert.AreEqual(result, (uint)100);
        }

        [TestMethod]
        public void AssertUpdateEntryWhileKeyIsNotInMap()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act
            map.Update(1, 100);

            //assert
            var result2 = map.Get(1, out var result);

            Assert.AreEqual(result, (uint)0);
            Assert.AreEqual(result2, false);
        }

        [TestMethod]
        public void AssertContainsEntryInMapReturnsProperValue()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);
            map.Emplace(1, 1);

            //act
            var result = map.Contains(1);

            //assert         
            Assert.AreEqual(true, result);
        }

        [TestMethod]
        public void AssertContainsEntryWhileKeyIsNotInMap()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act
            var result = map.Contains(1);

            //assert        
            Assert.AreEqual(result, false);
        }


        [TestMethod]
        public void AssertClearShouldResetMap()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act
            var result = map.Emplace(1, 1);
            map.Clear();

            //assert        
            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void AssertRemovingEntryWhileKeyIsNotInMap()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act
            var result = map.Remove(1);

            //assert
            Assert.AreEqual(result, false);
        }

        [TestMethod]
        public void AssertRemovingEntryShouldReduceCount()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);
            map.Emplace(1, 1);

            //act
            var result = map.Remove(1);

            //assert  
            Assert.AreEqual(result, true);
            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void AssertResizingShouldDoubleInSize()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 1);
            map.Emplace(2, 1);
            map.Emplace(3, 1);
            map.Emplace(4, 1);
            map.Emplace(5, 1);
            map.Emplace(6, 1);
            map.Emplace(7, 1);
            map.Emplace(8, 1);
            map.Emplace(9, 1);
            map.Emplace(10, 1);
            map.Emplace(11, 1);
            map.Emplace(12, 1);
            map.Emplace(13, 1);
            map.Emplace(14, 2);
            map.Emplace(15, 1);
            map.Emplace(16, 1);
            map.Emplace(17, 1);
            map.Emplace(18, 1);
            map.Emplace(19, 1);

            //assert  
            // 16 * 2) + 16

            Assert.AreEqual(48,(uint) map.Size);
        }

        [TestMethod]
        public void AssertResizingShouldSetProperCount()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 1);
            map.Emplace(2, 1);
            map.Emplace(3, 1);
            map.Emplace(4, 1);
            map.Emplace(5, 1);
            map.Emplace(6, 1);
            map.Emplace(7, 1);
            map.Emplace(8, 1);
            map.Emplace(9, 1);
            map.Emplace(10, 1);
            map.Emplace(11, 1);
            map.Emplace(12, 1);
            map.Emplace(13, 1);
            map.Emplace(14, 2);
            map.Emplace(15, 1);
            map.Emplace(16, 1);
            map.Emplace(17, 1);
            map.Emplace(18, 1);
            map.Emplace(19, 1);

            //assert  
            // 16 * 2) + 16

            Assert.AreEqual(19, map.Count);
        }

        [TestMethod]
        public void AssertRetrieveAfterResize()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 1);
            map.Emplace(2, 1);
            map.Emplace(3, 1);
            map.Emplace(4, 1);
            map.Emplace(5, 1);
            map.Emplace(6, 1);
            map.Emplace(7, 1);
            map.Emplace(8, 1);
            map.Emplace(9, 1);
            map.Emplace(10, 1);
            map.Emplace(11, 1);
            map.Emplace(12, 1);
            map.Emplace(13, 1);
            map.Emplace(14, 2);
            map.Emplace(15, 1);
            map.Emplace(16, 1);
            map.Emplace(17, 1);
            map.Emplace(18, 1);
            map.Emplace(19, 1);

            var result = map.Get(19, out var r1);
            //assert  

            Assert.AreEqual(19, map.Count);
            Assert.AreEqual(result, true);
            Assert.AreEqual(1, (int)r1);
        }

        [TestMethod]
        public void AssertRetrievalByIndexor()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 5);

            var result = map[1];

            //assert          
            Assert.AreEqual(5, (int)result);
        }

        [TestMethod]
        public void AssertUpdatingByIndexor()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 5);

            map[1] = 10;

            map.Get(1, out var r1);

            //assert          
            Assert.AreEqual(10, (int)r1);
        }

        [TestMethod]
        [ExpectedException(typeof(KeyNotFoundException))]
        public void AssertUpdatingByIndexorWhileKeyNotFoundShouldThrow()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 5);

            //throws
            map[5] = 10;
        }


        [TestMethod]
        [ExpectedException(typeof(KeyNotFoundException))]
        public void AssertRetrievingByIndexorWhileKeyNotFoundShouldThrow()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act    
            map.Emplace(1, 5);

            //throws
            var x = map[5];
        }

        [TestMethod]
        public void AssertEmplaceShouldIncreaseCount()
        {
            //arrange
            var map = new DenseMapSIMD<uint, uint>(16);

            //act
            map.Emplace(1, 1);

            //assert
            Assert.AreEqual(1, map.Count);
        }

        [TestMethod]
        public void AssertDailyUseCaseWithResize()
        {
            var fmap = new DenseMapSIMD<uint, uint>(16);

            foreach (var k in keys.Take(900000))
            {
                if (k == 1912101639)
                {

                }

                if (!fmap.Emplace(k, k))
                {
                    throw new InternalTestFailureException("Error occured while add");
                }
            }

            Assert.AreEqual(900000, fmap.Count);

            //find all entries from map

            //var offset = 0;
            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Get(k, out var result))
                {
                    throw new InternalTestFailureException("Error occured while get");
                }
            }

            //remove all entries from map

            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Remove(k))
                {
                    throw new InternalTestFailureException("Error occured while removing");
                }
            }

            Assert.IsTrue(fmap.Count == 0);

            //map full of tombstones, try inserting again

            foreach (var k in keys.Take(900000))
            {
                if (!fmap.Emplace(k, k))
                {
                    throw new InternalTestFailureException("Error occured while removing");
                }
            }

            Assert.AreEqual(900000, fmap.Count);
        }

        [TestMethod]
        public void AssertUnsuccesfulLookupReturnsFalse()
        {
            //arrange
            DenseMapSIMD<uint, uint> map = new DenseMapSIMD<uint, uint>(1000000);

            //act
            var found = map.Get(345, out var result);

            //assert
            Assert.AreEqual(false, found);
            Assert.IsTrue(result == 0);
        }

        [TestMethod]
        public void AssertCopyMapToAnother()
        {
            DenseMapSIMD<uint, uint> map = new DenseMapSIMD<uint, uint>(16);
            DenseMapSIMD<uint, uint> map2 = new DenseMapSIMD<uint, uint>(16);

            map.Emplace(1, 1);
            map.Emplace(2, 1);

            map2.Emplace(3, 1);
            map2.Emplace(4, 1);

            map.Copy(map2);

            Assert.IsTrue(4 == map.Count);
        }
    }
}
