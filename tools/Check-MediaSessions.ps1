<#
.SYNOPSIS
  Lists every Windows media session (GlobalSystemMediaTransportControls) with
  metadata, control flags, and a few timeline samples.

.NOTES
  Run with Windows PowerShell 5.1 (powershell.exe), which has built-in WinRT
  projection support. PowerShell 7 does not load WinRT types this way.

  Example:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\Check-MediaSessions.ps1 -Samples 3 -IntervalSeconds 3
#>
param(
  [int]$Samples = 3,
  [int]$IntervalSeconds = 3
)

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime] | Out-Null
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
  Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await($op, $resultType) {
  $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
  $task.Wait(-1) | Out-Null
  $task.Result
}

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$current = $mgr.GetCurrentSession()
"current session: " + $(if ($current) { $current.SourceAppUserModelId } else { '<none>' })
$sessions = @($mgr.GetSessions())
"session count: " + $sessions.Count

foreach ($s in $sessions) {
  $p = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
  $i = $s.GetPlaybackInfo()
  ""
  "app={0}" -f $s.SourceAppUserModelId
  "  status={0} type={1} rate={2} shuffle={3} repeat={4}" -f $i.PlaybackStatus, $i.PlaybackType, $i.PlaybackRate, $i.IsShuffleActive, $i.AutoRepeatMode
  "  title='{0}' artist='{1}' album='{2}' albumArtist='{3}' track={4}/{5} thumbnail={6}" -f $p.Title, $p.Artist, $p.AlbumTitle, $p.AlbumArtist, $p.TrackNumber, $p.AlbumTrackCount, ($null -ne $p.Thumbnail)
  "  controls: next={0} prev={1} toggle={2} play={3} pause={4} seek={5}" -f $i.Controls.IsNextEnabled, $i.Controls.IsPreviousEnabled, $i.Controls.IsPlayPauseToggleEnabled, $i.Controls.IsPlayEnabled, $i.Controls.IsPauseEnabled, $i.Controls.IsPlaybackPositionEnabled
  for ($n = 1; $n -le $Samples; $n++) {
    $t = $s.GetTimelineProperties()
    "  timeline {0}: wall={1:HH:mm:ss.fff} position={2} start={3} end={4} lastUpdated={5:HH:mm:ss.fff}" -f $n, (Get-Date), $t.Position, $t.StartTime, $t.EndTime, $t.LastUpdatedTime.LocalDateTime
    if ($n -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
  }
}
