// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using FhirPkg.Cache;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public class MemoryResourceCacheTests
{
    private sealed class TestResource
    {
        public string Value { get; set; } = string.Empty;
    }

    [Fact]
    public void Get_CachedEntry_ReturnsValue()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);
        TestResource resource = new TestResource { Value = "hello" };

        cache.Set("key1", resource);
        TestResource? result = cache.Get<TestResource>("key1");

        result.ShouldNotBeNull();
        result!.Value.ShouldBe("hello");
    }

    [Fact]
    public void Get_MissingEntry_ReturnsNull()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);

        TestResource? result = cache.Get<TestResource>("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void Set_ExceedsCapacity_EvictsOldest()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 2);

        cache.Set("key1", new TestResource { Value = "first" });
        cache.Set("key2", new TestResource { Value = "second" });
        cache.Set("key3", new TestResource { Value = "third" });

        // key1 should have been evicted (LRU)
        cache.Get<TestResource>("key1").ShouldBeNull();
        cache.Get<TestResource>("key2").ShouldNotBeNull();
        cache.Get<TestResource>("key3").ShouldNotBeNull();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);
        cache.Set("key1", new TestResource { Value = "a" });
        cache.Set("key2", new TestResource { Value = "b" });

        cache.Clear();

        cache.Count.ShouldBe(0);
        cache.Get<TestResource>("key1").ShouldBeNull();
        cache.Get<TestResource>("key2").ShouldBeNull();
    }

    [Fact]
    public void Count_ReflectsEntries()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);

        cache.Count.ShouldBe(0);

        cache.Set("key1", new TestResource { Value = "a" });
        cache.Count.ShouldBe(1);

        cache.Set("key2", new TestResource { Value = "b" });
        cache.Count.ShouldBe(2);
    }

    [Fact]
    public void Set_SameKey_UpdatesValue()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);

        cache.Set("key1", new TestResource { Value = "original" });
        cache.Set("key1", new TestResource { Value = "updated" });

        cache.Count.ShouldBe(1);
        cache.Get<TestResource>("key1")!.Value.ShouldBe("updated");
    }

    [Fact]
    public void Get_PromotesToMostRecentlyUsed()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 2);

        cache.Set("key1", new TestResource { Value = "first" });
        cache.Set("key2", new TestResource { Value = "second" });

        // Access key1 to promote it
        cache.Get<TestResource>("key1");

        // Adding key3 should evict key2 (now LRU), not key1
        cache.Set("key3", new TestResource { Value = "third" });

        cache.Get<TestResource>("key1").ShouldNotBeNull();
        cache.Get<TestResource>("key2").ShouldBeNull();
        cache.Get<TestResource>("key3").ShouldNotBeNull();
    }

    [Fact]
    public void Constructor_MaxEntriesLessThanOne_Throws()
    {
        Func<MemoryResourceCache> act = () => new MemoryResourceCache(maxEntries: 0);

        Should.Throw<ArgumentOutOfRangeException>(() => act());
    }

    [Fact]
    public void Get_TypeMismatch_ReturnsNull()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);
        cache.Set("key1", new TestResource { Value = "hello" });

        // Asking for a different type should return null
        List<string>? result = cache.Get<List<string>>("key1");

        result.ShouldBeNull();
    }

    [Fact]
    public void ConcurrentAccess_NoDataCorruption()
    {
        MemoryResourceCache cache = new MemoryResourceCache(maxEntries: 10);
        ConcurrentBag<Exception> exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, 200, i =>
        {
            try
            {
                string key = $"key{i % 20}";
                cache.Set(key, new TestResource { Value = $"value{i}" });
                cache.Get<TestResource>(key);

                if (i % 7 == 0)
                {
                    cache.Clear();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.ShouldBeEmpty();
        cache.Count.ShouldBeGreaterThanOrEqualTo(0);
        cache.Count.ShouldBeLessThanOrEqualTo(10);
    }
}
