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

    // --- MeterSlotDigits: the equip-card meter-body validator (width Signatures.KillsMeterSlotChars,
    //     alphabet digits + '/' + ' ' + '+' + the two letters of "to"). Additive: KillsDigits
    //     above is untouched and stays test-only after CardScanner/CardSites move to this one. ---

    private const int MeterWidth = 11; // Signatures.KillsMeterSlotChars; kept a literal here so this
                                        // suite doesn't itself depend on the production constant.

    [Theory]
    [InlineData("0/5 to +   ")]    // tier-0 body, padded (11 chars)
    [InlineData("49/50 to +3")]    // widest sub-max body, no padding needed (11 chars)
    [InlineData("55         ")]    // max-tier bare count, padded (11 chars)
    public void MeterSlotDigits_ascii_accepts_valid(string slot)
    {
        byte[] buf = Encoding.ASCII.GetBytes(slot);
        Assert.True(ByteScan.MeterSlotDigits(buf, 0, 1, MeterWidth), $"expected accept for '{slot}'");
    }

    [Fact]
    public void MeterSlotDigits_rejects_non_digit_first_char()
    {
        byte[] buf = Encoding.ASCII.GetBytes("abcdefghijk");
        Assert.False(ByteScan.MeterSlotDigits(buf, 0, 1, MeterWidth));
    }

    [Fact]
    public void MeterSlotDigits_rejects_a_char_outside_the_meter_alphabet()
    {
        // 'x' is not in {digits, '/', ' ', '+', 't', 'o'}.
        byte[] buf = Encoding.ASCII.GetBytes("0/5 xo +   ");
        Assert.False(ByteScan.MeterSlotDigits(buf, 0, 1, MeterWidth));
    }

    [Fact]
    public void MeterSlotDigits_utf16_rejects_nonzero_high_byte()
    {
        byte[] buf = Utf16Slot("49/50 to +3");
        buf[1] = 0x01; // high byte of '4' set -> not a clean UTF-16 ASCII char
        Assert.False(ByteScan.MeterSlotDigits(buf, 0, 2, MeterWidth));
    }

    [Fact]
    public void MeterSlotDigits_utf16_accepts_valid()
    {
        byte[] buf = Utf16Slot("49/50 to +3");
        Assert.True(ByteScan.MeterSlotDigits(buf, 0, 2, MeterWidth));
    }

    [Fact]
    public void MeterSlotDigits_rejects_too_short_buffer()
    {
        // Buffer only holds 5 of the required 11 chars.
        byte[] buf = Encoding.ASCII.GetBytes("0/5 t");
        Assert.False(ByteScan.MeterSlotDigits(buf, 0, 1, MeterWidth));
    }

    [Fact]
    public void MeterSlotDigits_bounds_safe_when_pos_plus_width_overflows_utf16_buffer()
    {
        byte[] buf = Utf16Slot("0/5 to +   ");
        Assert.False(ByteScan.MeterSlotDigits(buf, 4, 2, MeterWidth)); // pos != 0 -> runs past buf.Length
    }
}
