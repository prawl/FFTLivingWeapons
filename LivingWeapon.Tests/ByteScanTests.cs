using System.Text;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ByteScan.KillsDigits (the left-aligned slot validator for the "Kills NNNN" counter).
/// Accepts left-aligned digit strings padded with spaces ("0   ", "42  ", "1337", "0042").
/// Rejects: leading space, digit-after-space, non-digit/non-space, stale all-zero "0000"
/// is still valid (all digits, no space-after-digit rule is broken).
/// enc==1 = ASCII byte-per-char; enc==2 = UTF-16-LE (high byte must be 0x00).
/// </summary>
public class ByteScanTests
{
    // helper: build a 4-char ASCII buffer padded with leading junk so pos != 0 can be tested.
    private static byte[] AsciiSlot(string s)
    {
        // put the slot at offset 0 (the validator receives pos=0).
        return Encoding.ASCII.GetBytes(s);
    }

    private static byte[] Utf16Slot(string s)
    {
        return Encoding.Unicode.GetBytes(s);
    }

    // --- ASCII (enc==1) accepts ---

    [Theory]
    [InlineData("0   ")]   // single digit, 3 spaces
    [InlineData("42  ")]   // two digits, 2 spaces
    [InlineData("137 ")]   // three digits, 1 space
    [InlineData("1337")]   // four digits (no spaces)
    [InlineData("0042")]   // legacy four-digit form still valid
    public void KillsDigits_ascii_accepts_valid(string slot)
    {
        byte[] buf = AsciiSlot(slot);
        Assert.True(ByteScan.KillsDigits(buf, 0, 1), $"expected accept for '{slot}'");
    }

    // --- ASCII (enc==1) rejects ---

    [Theory]
    [InlineData("    ")]   // all spaces -> first char not a digit
    [InlineData(" 42 ")]   // leading space
    [InlineData("4 2 ")]   // digit after space (not left-aligned)
    [InlineData("abcd")]   // non-digit chars
    public void KillsDigits_ascii_rejects_invalid(string slot)
    {
        byte[] buf = AsciiSlot(slot);
        Assert.False(ByteScan.KillsDigits(buf, 0, 1), $"expected reject for '{slot}'");
    }

    // --- UTF-16 (enc==2) accepts ---

    [Theory]
    [InlineData("0   ")]
    [InlineData("42  ")]
    public void KillsDigits_utf16_accepts_valid(string slot)
    {
        byte[] buf = Utf16Slot(slot);
        Assert.True(ByteScan.KillsDigits(buf, 0, 2), $"utf16 expected accept for '{slot}'");
    }

    // --- UTF-16 (enc==2) rejects ---

    [Fact]
    public void KillsDigits_utf16_rejects_nonzero_high_byte()
    {
        // "0   " in UTF-16 but with the high byte of the first char set to 0x01 -> reject
        byte[] buf = Utf16Slot("0   ");
        buf[1] = 0x01;   // high byte of '0' -> not a clean UTF-16 ASCII char
        Assert.False(ByteScan.KillsDigits(buf, 0, 2));
    }

    [Fact]
    public void KillsDigits_utf16_rejects_leading_space()
    {
        byte[] buf = Utf16Slot(" 42 ");
        Assert.False(ByteScan.KillsDigits(buf, 0, 2));
    }
}
