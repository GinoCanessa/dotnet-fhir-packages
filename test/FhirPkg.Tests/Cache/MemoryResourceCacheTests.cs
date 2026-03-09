// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Cache;
using FluentAssertions;
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
        var cache = new MemoryResourceCache(maxEntries: 10);
        var resource = new TestResource { Value = "hello" };

        cache.Set("key1", resource);
        var result = cache.Get<TestResource>("key1");

        result.Should().NotBeNull();
        result!.Value.Should().Be("hello");
    }

    [Fact]
    public void Get_MissingEntry_ReturnsNull()
    {
        var cache = new MemoryResourceCache(maxEntries: 10);

        var result = cache.Get<TestResource>("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void Set_ExceedsCapacity_EvictsOldest()
    {
        var cache = new MemoryResourceCache(maxEntries: 2);

        cache.Set("key1", new TestResource { Value = "first" });
        cache.Set("key2", new TestResource { Value = "second" });
        cache.Set("key3", new TestResource { Value = "third" });

        // key1 should have been evicted (LRU)
        cache.Get<TestResource>("key1").Should().BeNull();
        cache.Get<TestResource>("key2").Should().NotBeNull();
        cache.Get<TestResource>("key3").Should().NotBeNull();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new MemoryResourceCache(maxEntries: 10);
        cache.Set("key1", new TestResource { Value = "a" });
        cache.Set("key2", new TestResource { Value = "b" });

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.Get<TestResource>("key1").Should().BeNull();
        cache.Get<TestResource>("key2").Should().BeNull();
    }

    [Fact]
    public void Count_ReflectsEntries()
    {
        var cache = new MemoryResourceCache(maxEntries: 10);

        cache.Count.Should().Be(0);

        cache.Set("key1", new TestResource { Value = "a" });
        cache.Count.Should().Be(1);

        cache.Set("key2", new TestResource { Value = "b" });
        cache.Count.Should().Be(2);
    }

    [Fact]
    public void Set_SameKey_UpdatesValue()
    {
        var cache = new MemoryResourceCache(maxEntries: 10);

        cache.Set("key1", new TestResource { Value = "original" });
        cache.Set("key1", new TestResource { Value = "updated" });

        cache.Count.Should().Be(1);
        cache.Get<TestResource>("key1")!.Value.Should().Be("updated");
    }

    [Fact]
    public void Get_PromotesToMostRecentlyUsed()
    {
        var cache = new MemoryResourceCache(maxEntries: 2);

        cache.Set("key1", new TestResource { Value = "first" });
        cache.Set("key2", new TestResource { Value = "second" });

        // Access key1 to promote it
        cache.Get<TestResource>("key1");

        // Adding key3 should evict key2 (now LRU), not key1
        cache.Set("key3", new TestResource { Value = "third" });

        cache.Get<TestResource>("key1").Should().NotBeNull();
        cache.Get<TestResource>("key2").Should().BeNull();
        cache.Get<TestResource>("key3").Should().NotBeNull();
    }

    [Fact]
    public void Constructor_MaxEntriesLessThanOne_Throws()
    {
        var act = () => new MemoryResourceCache(maxEntries: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Get_TypeMismatch_ReturnsNull()
    {
        var cache = new MemoryResourceCache(maxEntries: 10);
        cache.Set("key1", new TestResource { Value = "hello" });

        // Asking for a different type should return null
        var result = cache.Get<List<string>>("key1");

        result.Should().BeNull();
    }
}
