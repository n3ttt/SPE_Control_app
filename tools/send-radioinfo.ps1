# Sends one N1MM/TTTLog-style RadioInfo packet, for testing band-follow without a radio.
#   .\tools\send-radioinfo.ps1 -kHz 7025 -Mode CW
param([double]$kHz = 14025, [string]$Mode = 'CW', [switch]$Transmitting, [string]$HostName = '127.0.0.1', [int]$Port = 12060)
$f = [string][int64][Math]::Round($kHz * 100)
$tx = if ($Transmitting) { 'True' } else { 'False' }
$xml = '<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<RadioInfo><app>tttlog</app><StationName>TEST</StationName><RadioNr>1</RadioNr>' +
       "<Freq>$f</Freq><TXFreq>$f</TXFreq><Mode>$Mode</Mode><mycall>N0CALL</mycall><OpCall>N0CALL</OpCall><IsRunning>False</IsRunning>" +
       '<FocusEntry>0</FocusEntry><EntryWindowHwnd>0</EntryWindowHwnd><Antenna>0</Antenna><Rotors></Rotors><FocusRadioNr>1</FocusRadioNr>' +
       "<IsStereo>False</IsStereo><IsSplit>False</IsSplit><ActiveRadioNr>1</ActiveRadioNr><IsTransmitting>$tx</IsTransmitting>" +
       '<FunctionKeyCaption></FunctionKeyCaption><RadioName></RadioName><AuxAntSelected>-1</AuxAntSelected><AuxAntSelectedName></AuxAntSelectedName>' +
       '<IsConnected>True</IsConnected></RadioInfo>'
$u = New-Object System.Net.Sockets.UdpClient
$b = [Text.Encoding]::UTF8.GetBytes($xml)
[void]$u.Send($b, $b.Length, $HostName, $Port)
$u.Close()
