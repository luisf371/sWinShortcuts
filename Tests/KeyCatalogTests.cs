using System.Windows.Input;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public sealed class KeyCatalogTests
{
    [Theory]
    [InlineData(Key.F13)]
    [InlineData(Key.F24)]
    [InlineData(Key.PrintScreen)]
    [InlineData(Key.Pause)]
    [InlineData(Key.Scroll)]
    [InlineData(Key.NumLock)]
    [InlineData(Key.Oem102)]
    [InlineData(Key.Apps)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightCtrl)]
    public void AssignableKeys_KeysOutsideCommonCatalog_AreIncluded(Key key)
    {
        Assert.Contains(key, KeyCatalog.AssignableKeys);
        Assert.True(KeyCatalog.IsAssignable(key));
    }

    [Fact]
    public void AssignableKeys_EveryCommonKey_IsIncluded()
    {
        Assert.All(KeyCatalog.GetCommonKeys(), key => Assert.Contains(key, KeyCatalog.AssignableKeys));
    }

    [Theory]
    [InlineData(Key.None)]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.DeadCharProcessed)]
    [InlineData(Key.System)]
    public void AssignableKeys_PseudoKeys_AreExcluded(Key key)
    {
        Assert.DoesNotContain(key, KeyCatalog.AssignableKeys);
    }

    [Fact]
    public void AssignableKeys_EveryKey_HasAVirtualKey()
    {
        Assert.All(KeyCatalog.AssignableKeys, key => Assert.NotEqual(0, KeyInteropUtilities.ToVirtualKey(key)));
    }

    [Fact]
    public void AltShortcutKeys_AssignableKeysWithoutAlt_MatchExactly()
    {
        Assert.DoesNotContain(Key.LeftAlt, KeyCatalog.AltShortcutKeys);
        Assert.DoesNotContain(Key.RightAlt, KeyCatalog.AltShortcutKeys);
        Assert.Contains(Key.F13, KeyCatalog.AltShortcutKeys);
        Assert.Equal(KeyCatalog.AssignableKeys.Count - 2, KeyCatalog.AltShortcutKeys.Count);
        Assert.All(KeyCatalog.AltShortcutKeys, key => Assert.Contains(key, KeyCatalog.AssignableKeys));
    }
}
