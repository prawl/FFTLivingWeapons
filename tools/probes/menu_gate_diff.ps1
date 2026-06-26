param([string]$mode = "cap", [string]$label = "test", [string]$startHex = "436BE00000", [string]$lenHex = "200000", [int]$n = 3)

# Differential memory capture/diff. Region is configurable so we can target either the UI widget
# arena (0x436BExxxxx) or the battle targeting struct (0x140C6xxxx).
# cap  <label>  : read the region N times (rejects per-state noise) -> TEMP\greycap\<label>.bin
# diff <A:B>    : candidate iff constant across all A reads, constant across all B reads, A!=B.

$START = [Convert]::ToInt64($startHex, 16)
$LEN   = [Convert]::ToInt64($lenHex, 16)
$N     = $n
$GAPMS = 80
$DIR   = "$env:TEMP\greycap"
New-Item -ItemType Directory -Force -Path $DIR | Out-Null

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RPM {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool inh, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@

function Read-Region([IntPtr]$h) {
  $buf = New-Object byte[] $LEN
  $pg  = New-Object byte[] 0x1000
  $read = 0
  for ($p = 0; $p -lt $LEN; $p += 0x1000) {
    if ([RPM]::ReadProcessMemory($h, [IntPtr]($START + $p), $pg, 0x1000, [ref]$read) -and $read -eq 0x1000) {
      [Array]::Copy($pg, 0, $buf, $p, 0x1000)
    }
    # unmapped page -> left as zero (a 0-vs-0 offset can never be a candidate)
  }
  return ,$buf
}

$gp = Get-Process fft_enhanced -ErrorAction SilentlyContinue
if (-not $gp) { Write-Host "GAME NOT RUNNING"; exit 1 }

if ($mode -eq "cap") {
  $h = [RPM]::OpenProcess(0x0410, $false, $gp.Id)
  $all = New-Object byte[] ($LEN * $N)
  for ($s = 0; $s -lt $N; $s++) {
    $snap = Read-Region $h
    [Array]::Copy($snap, 0, $all, $s * $LEN, $LEN)
    Start-Sleep -Milliseconds $GAPMS
  }
  [RPM]::CloseHandle($h) | Out-Null
  [System.IO.File]::WriteAllBytes("$DIR\$label.bin", $all)
  Write-Host "captured $N x $LEN bytes -> $DIR\$label.bin (pid $($gp.Id))"
}
elseif ($mode -eq "diff") {
  $parts = $label.Split(":")
  $A = [System.IO.File]::ReadAllBytes("$DIR\$($parts[0]).bin")
  $B = [System.IO.File]::ReadAllBytes("$DIR\$($parts[1]).bin")
  $nA = $A.Length / $LEN; $nB = $B.Length / $LEN
  # fast first pass: offsets where snapshot0 differs
  $hits = New-Object System.Collections.Generic.List[int]
  for ($o = 0; $o -lt $LEN; $o++) { if ($A[$o] -ne $B[$o]) { $hits.Add($o) } }
  Write-Host "raw snapshot0 diffs: $($hits.Count)"
  $cands = 0
  foreach ($o in $hits) {
    $a0 = $A[$o]; $ok = $true
    for ($s = 1; $s -lt $nA; $s++) { if ($A[$s*$LEN+$o] -ne $a0) { $ok = $false; break } }
    if (-not $ok) { continue }
    $b0 = $B[$o]
    for ($s = 1; $s -lt $nB; $s++) { if ($B[$s*$LEN+$o] -ne $b0) { $ok = $false; break } }
    if (-not $ok) { continue }
    $addr = $START + $o
    Write-Host ("CANDIDATE 0x{0:X}  {1}={2:X2}  {3}={4:X2}" -f $addr, $parts[0], $a0, $parts[1], $b0)
    $cands++
    if ($cands -ge 60) { Write-Host "...(capping output at 60)"; break }
  }
  Write-Host "--- $cands stable candidate byte(s) ---"
}
elseif ($mode -eq "isect") {
  # label = "gray:notgray:gray2:notgray2" -- keep offsets that flip the SAME way in BOTH pairs
  $p = $label.Split(":")
  $G1=[System.IO.File]::ReadAllBytes("$DIR\$($p[0]).bin"); $N1=[System.IO.File]::ReadAllBytes("$DIR\$($p[1]).bin")
  $G2=[System.IO.File]::ReadAllBytes("$DIR\$($p[2]).bin"); $N2=[System.IO.File]::ReadAllBytes("$DIR\$($p[3]).bin")
  $nn = $G1.Length / $LEN
  function Stable($buf,$o){ $v=$buf[$o]; for($s=1;$s -lt $nn;$s++){ if($buf[$s*$LEN+$o] -ne $v){return $false} }; return $true }
  $cands = 0
  for ($o=0; $o -lt $LEN; $o++) {
    if ($G1[$o] -eq $N1[$o]) { continue }                          # didn't flip in pair 1
    if ($G1[$o] -ne $G2[$o] -or $N1[$o] -ne $N2[$o]) { continue }  # different values across pairs => unit noise
    if (-not (Stable $G1 $o) -or -not (Stable $N1 $o) -or -not (Stable $G2 $o) -or -not (Stable $N2 $o)) { continue }
    Write-Host ("SURVIVOR 0x{0:X}  gray={1:X2}  notgray={2:X2}" -f ($START+$o), $G1[$o], $N1[$o])
    $cands++
  }
  Write-Host "--- $cands survivor byte(s) across both toggles ---"
}