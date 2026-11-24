# Offline resource-catalog check for the interface languages. Needs no SOLIDWORKS and
# no build: it reads the .resx files and the form sources directly.
param([string]$Root = "$PSScriptRoot/..")
$ErrorActionPreference = 'Stop'
$ui = Join-Path $Root 'SW2URDF/UI'
$failures = New-Object System.Collections.ArrayList
function Fail([string]$message) { [void]$failures.Add($message) }

function Read-Catalog([string]$name) {
    $path = Join-Path $ui $name
    if (-not (Test-Path -LiteralPath $path)) { Fail "missing resource file $name"; return $null }
    $values = [ordered]@{}
    foreach ($entry in ([xml](Get-Content -LiteralPath $path -Raw -Encoding UTF8)).root.data) {
        $key = [string]$entry.name
        if ($values.Contains($key)) { Fail "$name duplicate key '$key'" }
        $values[$key] = [string]$entry.value
    }
    # ResGen compares keys case-insensitively, so keys differing only by case are duplicates.
    $values.Keys | Group-Object { $_.ToLowerInvariant() } | Where-Object Count -gt 1 |
        ForEach-Object { Fail "$name keys differ only by case: $($_.Group -join ' | ')" }
    $values.Keys | Where-Object { $_ -cne $_.ToLowerInvariant() } |
        ForEach-Object { Fail "$name key '$_' is not lowercase" }
    $values.Keys | Where-Object { [string]::IsNullOrWhiteSpace($values[$_]) } |
        ForEach-Object { Fail "$name empty value for '$_'" }
    return $values
}

$english = Read-Catalog 'Strings.resx'
$chinese = Read-Catalog 'Strings.zh-TW.resx'
if ($english -and $chinese) {
    foreach ($k in $english.Keys) { if (-not $chinese.Contains($k)) { Fail "zh-TW lacks key '$k'" } }
    foreach ($k in $chinese.Keys) { if (-not $english.Contains($k)) { Fail "English lacks key '$k'" } }
    foreach ($k in $english.Keys) {
        if ($chinese.Contains($k)) {
            $a = ([regex]::Matches($english[$k], '\{\d+\}') | ForEach-Object Value | Sort-Object) -join ','
            $b = ([regex]::Matches($chinese[$k], '\{\d+\}') | ForEach-Object Value | Sort-Object) -join ','
            if ($a -ne $b) { Fail "placeholders differ for '$k'" }
        }
    }
    # The English catalog must read exactly like the code it stands in for: the key is the
    # lowercase caption, and the English value may only differ from it by capitalization.
    foreach ($k in $english.Keys) {
        if ($english[$k].ToLowerInvariant() -ne $k) { Fail "English value for '$k' differs from its key: '$($english[$k])'" }
    }
    # Static captions of both forms must be translated unless they are symbols or protocol names.
    $literal = '"((?:[^"\\]|\\.)*)"'
    $invariant = @('*', '1', 'x', 'y', 'z', 'ixx', 'ixy', 'ixz', 'iyy', 'iyz', 'izz', 'ROS 1', 'ROS 2', 'pos = multiplier * pos_other + offset')
    foreach ($form in 'AssemblyExportForm', 'PartExportForm') {
        $src = Get-Content -LiteralPath (Join-Path $ui "$form.Designer.cs") -Raw -Encoding UTF8
        foreach ($m in [regex]::Matches($src, '\bText\s*=\s*(' + $literal + '(?:\s*\+\s*' + $literal + ')*)')) {
            $caption = (([regex]::Matches($m.Groups[1].Value, $literal) | ForEach-Object { $_.Groups[1].Value }) -join '').Replace("\'", "'")
            if ($caption -and ($invariant -cnotcontains $caption) -and -not $chinese.Contains($caption.ToLowerInvariant())) {
                Fail "$form caption has no translation: '$caption'"
            }
        }
    }
    # Punctuation around Latin text in Traditional Chinese. Full-width brackets and colons next to
    # Latin text, numbers or placeholders are drawn with wide gaps, so a zh-TW text that contains any
    # of those uses half-width ( ) : instead: a space before "(", a space after ":" (unless it ends
    # the text). Texts in Chinese only keep full-width marks. Angles are written "deg" in both languages.
    $fullWidth = '[' + [char]0xFF08 + [char]0xFF09 + [char]0xFF1A + ']'
    foreach ($k in $chinese.Keys) {
        $v = $chinese[$k]
        if ($v -notmatch '[A-Za-z0-9]') { continue }
        if ($v -match $fullWidth) { Fail "zh-TW '$k' mixes Latin text with full-width brackets or colons: '$v'" }
        if ($v -match '[^\s(\[' + [char]0x300C + ']\(') { Fail "zh-TW '$k' needs a space before '(': '$v'" }
        if ($v -match ':[^\s/\\]') { Fail "zh-TW '$k' needs a space after ':': '$v'" }
    }
    # The note under the joint page names the model file, which may be a .urdf or a .xacro file.
    $note = [regex]::Match((Get-Content -LiteralPath (Join-Path $ui 'AssemblyExportForm.Designer.cs') -Raw -Encoding UTF8), 'label4\.Text = "([^"]*)"').Groups[1].Value
    if ($note -notmatch 'model file') { Fail "the blank-field note does not name the model file: '$note'" }
    elseif (-not $chinese.Contains($note.ToLowerInvariant()) -or $chinese[$note.ToLowerInvariant()] -ne ((-join [char[]](0x7A7A, 0x767D, 0x6B04, 0x4F4D, 0x4E0D, 0x6703, 0x5BEB, 0x5165, 0x6A21, 0x578B, 0x6A94, 0x3002)))) {
        Fail "the zh-TW blank-field note is not the agreed text"
    }
    # The joint page description is two sentences; in zh-TW it breaks after the first one instead of
    # wherever the width ends (Chinese wraps between any two characters, splitting words).
    $descriptionKey = 'customize the joint properties. if you want to adjust the coordinate systems and axes in the model, click cancel and restart the export. the tool will recognize your changes on the next run.'
    $period = [string][char]0x3002
    if (-not $chinese.Contains($descriptionKey)) { Fail 'the joint page description has no zh-TW text' }
    elseif ($chinese[$descriptionKey] -notmatch ([regex]::Escape($period) + '\r?\n\S')) { Fail "the zh-TW joint page description does not break after its first sentence: '$($chinese[$descriptionKey])'" }
    # The export buttons of the link page name the model file too, not URDF.
    $designer = Get-Content -LiteralPath (Join-Path $ui 'AssemblyExportForm.Designer.cs') -Raw -Encoding UTF8
    $modelFile = -join [char[]](0x6A21, 0x578B, 0x6A94)
    $agreed = @{
        'buttonLinksExportUrdfOnly' = (-join [char[]](0x50C5, 0x532F, 0x51FA)) + $modelFile + [char]0x2026
        'buttonLinksFinish' = (-join [char[]](0x532F, 0x51FA)) + $modelFile + (-join [char[]](0x8207, 0x7DB2, 0x683C)) + [char]0x2026
    }
    foreach ($button in $agreed.Keys) {
        $caption = [regex]::Match($designer, [regex]::Escape($button) + '\.Text = "([^"]*)"').Groups[1].Value
        if ($caption -notmatch 'Model File') { Fail "the $button caption does not name the model file: '$caption'" }
        elseif ($chinese[$caption.ToLowerInvariant()] -ne $agreed[$button]) { Fail "the zh-TW $button caption is not the agreed text" }
    }
    # Every caption the code looks up needs a resource, and every resource must be looked
    # up or shown somewhere in this tree (no orphans from strings that live in other branches).
    $used = New-Object System.Collections.Generic.HashSet[string]
    foreach ($file in Get-ChildItem -Path (Join-Path $Root 'SW2URDF') -Recurse -Filter *.cs) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($m in [regex]::Matches($text, '(?:\bui\.|(?<![\w.]))Get\(' + $literal + '\)')) {
            $key = $m.Groups[1].Value.Replace('\"', '"').Replace('\n', "`n").Replace('\r', "`r").Replace('\\', '\').ToLowerInvariant()
            [void]$used.Add($key)
            if (-not $chinese.Contains($key)) { Fail "$($file.Name) uses untranslated '$($m.Groups[1].Value)'" }
        }
        # Combo-box protocol values are shown through ComboDisplay, not Get("..."), so their
        # catalog entries are referenced from UiText.ProtocolValues.
        if ($file.Name -eq 'UiText.cs') {
            $block = [regex]::Match($text, 'ProtocolValues\s*=\s*\{([^}]*)\}')
            foreach ($m in [regex]::Matches($block.Groups[1].Value, $literal)) {
                $key = $m.Groups[1].Value.ToLowerInvariant()
                [void]$used.Add($key)
                if (-not $chinese.Contains($key)) { Fail "UiText protocol value '$($m.Groups[1].Value)' is untranslated" }
            }
        }
        if ($file.Name -match '^(Assembly|Part)ExportForm\.Designer\.cs$') {
            foreach ($m in [regex]::Matches($text, '\bText\s*=\s*(' + $literal + '(?:\s*\+\s*' + $literal + ')*)')) {
                $caption = (([regex]::Matches($m.Groups[1].Value, $literal) | ForEach-Object { $_.Groups[1].Value }) -join '').Replace("\'", "'")
                [void]$used.Add($caption.ToLowerInvariant())
            }
        }
    }
    foreach ($k in $chinese.Keys) { if (-not $used.Contains($k)) { Fail "resource '$k' is not used by any caption or ui.Get call" } }
}
if ($failures.Count) { $failures | ForEach-Object { Write-Output "FAIL: $_" }; exit 1 }
Write-Output "PASS: catalogs match ($($english.Count) keys), no case-only duplicates, placeholders and form captions covered"
