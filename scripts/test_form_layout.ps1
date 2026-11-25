# Offline layout check for the export forms. Does not connect to SOLIDWORKS or register COM.
# Builds each form without its constructor (which needs SOLIDWORKS), runs only the
# designer-generated InitializeComponent, and checks that on every form page
#   - no two sibling controls overlap,
#   - no control extends past its container,
#   - no fixed-size label clips its text,
# for several system fonts and display-scaling factors. Display scaling is simulated by
# enlarging the form font, which is what AutoScaleMode.Font reacts to.
# Pure PowerShell (no Add-Type compilation), so every file that runs is a signed script
# plus signed assemblies.
param(
    [string]$AssemblyPath = "$PSScriptRoot/../SW2URDF/bin/x64/Debug/SW2URDF.dll",
    [string]$ScreenshotDirectory = '',
    [switch]$ShowAll
)
$ErrorActionPreference = 'Stop'
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$directory = Split-Path -Parent $AssemblyPath
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'log4net.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)

$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'

# The forms are limited to the work area of the screen they are on. Give them a large one, so that the
# result does not depend on the screen the test runs on. Windows still limits a window to about the size
# of the real screen: the checks that need more are skipped, and each is listed.
$areaField = $assembly.GetType('SW2URDF.UI.UiLayout', $true).GetField('TestWorkingArea', [Reflection.BindingFlags]'Static,NonPublic,Public')
if ($areaField) { $areaField.SetValue($null, (New-Object System.Drawing.Rectangle(0, 0, 7680, 4320))) }
$skipped = New-Object System.Collections.ArrayList

function New-Form([Type]$type, [System.Drawing.Font]$font, [single]$dpi, $ui = $null) {
    $form = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
    [void][System.Windows.Forms.Form].GetConstructor($flags, $null, [Type[]]@(), $null).Invoke($form, @())
    if ($font) { $form.Font = $font }
    try { [void]$type.GetMethod('InitializeComponent', $flags).Invoke($form, @()) } catch { throw $_.Exception.InnerException }
    $events = [System.ComponentModel.Component].GetProperty('Events', $flags).GetValue($form, @())
    $loadKey = [System.Windows.Forms.Form].GetField('EVENT_LOAD', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $events.RemoveHandler($loadKey, $events[$loadKey])
    # Translated the way the constructors translate, before the layout is fitted below.
    if ($ui) {
        $field = $type.GetField('ui', $flags)
        if ($field) { $field.SetValue($form, $ui) }
        $ui.Apply($form)
        $align = $type.GetMethod('AlignFieldLabels', $flags)
        if ($align) { [void]$align.Invoke($form, @()) }
    }
    # The constructors also grow too-short push buttons (UiLayout); check that state at the DPI being simulated.
    $layoutType = $assembly.GetType('SW2URDF.UI.UiLayout')
    # FitForm is what the constructors call: it fits the buttons, spaces the rows and makes the form
    # taller by what that added.
    $fitForm = if ($layoutType) { $layoutType.GetMethod('FitForm', [Reflection.BindingFlags]'Static,NonPublic,Public') }
    if ($fitForm) { [void]$fitForm.Invoke($null, @($form, $dpi)) } else {
        $fit = if ($layoutType) { $layoutType.GetMethod('FitButtonHeights', [Reflection.BindingFlags]'Static,NonPublic,Public') }
        if ($fit) { [void]$fit.Invoke($null, @($form, $dpi)) }
        $space = if ($layoutType) { $layoutType.GetMethod('SpaceRows', [Reflection.BindingFlags]'Static,NonPublic,Public') }
        if ($space) { [void]$space.Invoke($null, @($form, $dpi)) }
    }
    # Banner with the active configuration: the text is only known at run time, so check the longest case.
    $banner = $form.Controls.Find('labelConfigurationStatus', $true) | Select-Object -First 1
    $layoutHelper = $assembly.GetType('SW2URDF.UI.UiLayout')
    if ($banner -and $layoutHelper) {
        $sample = "Active configuration: A_rather_long_configuration_name_2000`r`nUsing the shared (legacy) settings; they are stored for this configuration on the next save."
        if ($font -and $font.Name -ne 'Microsoft Sans Serif') { $sample = [regex]::Unescape('\u4f7f\u7528\u4e2d\u7684\u7d44\u614b\uff1a\u9019\u662f\u4e00\u500b\u76f8\u7576\u9577\u7684\u7d44\u614b\u540d\u7a31\u4e8c\u96f6\u96f6\u96f6\r\n\u76ee\u524d\u4f7f\u7528\u5171\u7528\uff08\u820a\u7248\uff09\u8a2d\u5b9a\uff1b\u4e0b\u6b21\u5132\u5b58\u6642\u6703\u5b58\u5165\u6b64\u7d44\u614b\u3002') }
        [void]$layoutHelper.GetMethod('SetStatusBannerText', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, [object[]]@([System.Windows.Forms.Label]$banner, [string]$sample))
    }
    # Required-field stars ('*' labels) are hidden in the designer and shown at run time for some
    # joint types; show them all so they are checked against the group titles they follow.
    $stack = New-Object System.Collections.Stack; $stack.Push($form)
    while ($stack.Count -gt 0) {
        foreach ($c in $stack.Pop().Controls) {
            if (($c -is [System.Windows.Forms.Label]) -and $c.Text -eq '*') { $c.Visible = $true }
            $stack.Push($c)
        }
    }
    # Last step of the constructors: what the form does with room beyond its default size.
    $grow = $type.GetMethod('SetUpGrowLayout', $flags)
    if ($grow) { [void]$grow.Invoke($form, @()) }
    return $form
}

# Control.Visible is false while the form is not shown; read the control's own flag.
$getState = [System.Windows.Forms.Control].GetMethod('GetState', $flags, $null, [Type[]]@([int]), $null)
function Test-Shown([System.Windows.Forms.Control]$c) { return [bool]$getState.Invoke($c, @(2)) }

# Rectangle a control occupies, in its parent's client coordinates.
# A fixed-size, top-left aligned label only occupies the space its text needs; its
# empty remainder may sit under a neighbour without any visible collision.
function Get-Box([System.Windows.Forms.Control]$c) {
    $b = $c.Bounds
    if (($c -is [System.Windows.Forms.Label]) -and -not $c.AutoSize -and $c.TextAlign -eq 'TopLeft' -and -not [string]::IsNullOrEmpty($c.Text)) {
        $format = [System.Windows.Forms.TextFormatFlags]::WordBreak -bor [System.Windows.Forms.TextFormatFlags]::NoPadding
        $need = [System.Windows.Forms.TextRenderer]::MeasureText($c.Text, $c.Font, (New-Object System.Drawing.Size($c.Width, 0)), $format)
        return New-Object System.Drawing.Rectangle($b.X, $b.Y, [Math]::Min($b.Width, $need.Width), [Math]::Min($b.Height, $need.Height))
    }
    return $b
}

function Test-TextClipped([System.Windows.Forms.Control]$c) {
    if (-not ($c -is [System.Windows.Forms.Label]) -or $c.AutoSize -or [string]::IsNullOrEmpty($c.Text)) { return $false }
    $format = [System.Windows.Forms.TextFormatFlags]::WordBreak -bor [System.Windows.Forms.TextFormatFlags]::NoPadding
    $need = [System.Windows.Forms.TextRenderer]::MeasureText($c.Text, $c.Font, (New-Object System.Drawing.Size($c.Width, 0)), $format)
    return ($need.Height -gt $c.Height + 1)
}

function Test-Container([System.Windows.Forms.Control]$parent, [string]$path, [bool]$all, $problems) {
    $kids = @($parent.Controls | Where-Object { $all -or (Test-Shown $_) })
    $client = $parent.ClientSize
    foreach ($c in $kids) {
        $b = Get-Box $c
        if (-not $parent.AutoScroll -and ($b.Right -gt $client.Width + 1 -or $b.Bottom -gt $client.Height + 1 -or $b.Left -lt -1 -or $b.Top -lt -1)) {
            [void]$problems.Add("outside ${path}: $($c.Name) $b in $client")
        }
        # A scrolling container may extend past the window, but its navigation buttons must not scroll away
        # with the content: they belong in a fixed (non-scrolling) panel that is always visible.
        if ($parent.AutoScroll -and ($c -is [System.Windows.Forms.Button]) -and $c.Name -match 'Cancel|Finish|Next|Previous|ExportUrdfOnly') {
            [void]$problems.Add("button scrolls away ${path}: $($c.Name) is inside the scrolling container $($parent.Name)")
        }
        if (Test-TextClipped $c) { [void]$problems.Add("text clipped ${path}: $($c.Name) '$($c.Text)' $b") }
        # Fonts must follow the form font (system UI font, display scaling) so text sizes stay
        # proportional to the scaled layout: same family, size within 0.9x-1.3x (bold headings).
        if ($script:baseFont -and -not [string]::IsNullOrEmpty($c.Text) -and ($c -is [System.Windows.Forms.Label] -or $c -is [System.Windows.Forms.ButtonBase] -or $c -is [System.Windows.Forms.GroupBox])) {
            $ratio = $c.Font.SizeInPoints / $script:baseFont.SizeInPoints
            if ($c.Font.FontFamily.Name -ne $script:baseFont.FontFamily.Name -or $ratio -lt 0.9 -or $ratio -gt 1.3) {
                [void]$problems.Add("font ${path}: $($c.Name) $($c.Font.Name) $($c.Font.SizeInPoints)pt vs form $($script:baseFont.Name) $($script:baseFont.SizeInPoints)pt")
            }
        }
    }
    for ($i = 0; $i -lt $kids.Count; $i++) {
        for ($j = $i + 1; $j -lt $kids.Count; $j++) {
            # A label paints an opaque background over its whole bounds, so its empty remainder
            # may touch a neighbour but must not cover the neighbour's text or box.
            $a = Get-Box $kids[$i]; $b = Get-Box $kids[$j]
            $x1 = [System.Drawing.Rectangle]::Intersect($a, $kids[$j].Bounds)
            $x2 = [System.Drawing.Rectangle]::Intersect($kids[$i].Bounds, $b)
            if (($x1.Width -gt 1 -and $x1.Height -gt 1) -or ($x2.Width -gt 1 -and $x2.Height -gt 1)) {
                [void]$problems.Add("overlap ${path}: $($kids[$i].Name) $($kids[$i].Bounds) x $($kids[$j].Name) $($kids[$j].Bounds)")
            }
        }
    }
    foreach ($c in $kids) {
        if ($c.Controls.Count -gt 0 -and -not ($c -is [System.Windows.Forms.TreeView]) -and -not ($c -is [System.Windows.Forms.NumericUpDown]) -and -not ($c -is [System.Windows.Forms.DomainUpDown]) -and -not ($c -is [System.Windows.Forms.ComboBox])) {
            Test-Container $c "$path/$($c.Name)" $all $problems
        }
    }
}

# Each entry is a form page: the controls that are on screen together.
function Get-Pages($form) {
    $pages = @()
    $panel = $form.Controls.Find('panelLinkProperties', $false)
    if ($panel.Count -eq 1) {
        $pages += ,@('joint page', $form, $panel[0])
        $pages += ,@('link page', $panel[0], $panel[0])
    } else {
        $pages += ,@('form', $form, $null)
    }
    return ,$pages
}

$formTypes = @('SW2URDF.UI.AssemblyExportForm', 'SW2URDF.UI.PartExportForm')
$fonts = @(
    @('Microsoft Sans Serif', 8.25), @('Microsoft JhengHei UI', 9), @('PMingLiU', 9)
)
$scales = @(1.0, 1.25, 1.5, 1.75, 2.0)
$total = 0
$report = New-Object System.Collections.ArrayList
foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($f in $fonts) {
        foreach ($scale in $scales) {
            $font = New-Object System.Drawing.Font($f[0], [single]($f[1] * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
            $script:baseFont = $font
            $form = New-Form $type $font ([single](96 * $scale))
            try {
                $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
                $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
                $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                foreach ($page in (Get-Pages $form)) {
                    $problems = New-Object System.Collections.ArrayList
                    $panel = $page[2]
                    # The link page panel covers the joint page; show one page at a time.
                    if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
                    if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
                    [System.Windows.Forms.Application]::DoEvents()
                    Test-Container $page[1] ($page[0]) $ShowAll.IsPresent $problems
                    $label = "$($type.Name) [$($f[0]) $($f[1])pt x$scale] $($page[0])"
                    foreach ($p in $problems) { [void]$report.Add("$label : $p") }
                    $total += $problems.Count
                    if ($ScreenshotDirectory) {
                        $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
                        $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)))
                        $name = ("{0}_{1}_{2}_{3}" -f $type.Name, ($f[0] -replace ' ', ''), $scale, ($page[0] -replace ' ', ''))
                        $bmp.Save((Join-Path $ScreenshotDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                        $bmp.Dispose()
                    }
                }
            } finally { $form.Dispose(); $font.Dispose() }
        }
    }
}

# Window size helpers. The checks build the forms the way the constructors do, translated, with the
# font each form uses for its UI language (UiText.CreateFormFont where the form chooses one, the
# default font otherwise), scaled for 100-175 %.
$uiType = $assembly.GetType('SW2URDF.UI.UiText', $true)
$createFont = $uiType.GetMethod('CreateFormFont', $flags)

function Get-FormFont($ui, [double]$scale) {
    $base = if ($createFont) { $createFont.Invoke($ui, @()) } else { [System.Windows.Forms.Control]::DefaultFont }
    return New-Object System.Drawing.Font($base.FontFamily, [single]($base.SizeInPoints * $scale), $base.Style, [System.Drawing.GraphicsUnit]::Point, $base.GdiCharSet)
}

# Captions stacked in a column (a group title and the caption under it) must not touch.
function Test-Touching([System.Windows.Forms.Control]$parent, [string]$path, $problems) {
    $labels = @($parent.Controls | Where-Object { ($_ -is [System.Windows.Forms.Label]) -and (Test-Shown $_) -and -not [string]::IsNullOrEmpty($_.Text) })
    for ($i = 0; $i -lt $labels.Count; $i++) {
        for ($j = 0; $j -lt $labels.Count; $j++) {
            if ($i -eq $j) { continue }
            $a = Get-Box $labels[$i]; $b = Get-Box $labels[$j]
            $sideBySide = [Math]::Min($a.Right, $b.Right) - [Math]::Max($a.Left, $b.Left)
            $gap = $b.Top - $a.Bottom
            if ($a.Top -le $b.Top -and $sideBySide -gt 1 -and $gap -lt 1 -and $gap -gt -2) {
                [void]$problems.Add("touching ${path}: $($labels[$i].Name) '$($labels[$i].Text)' $a above $($labels[$j].Name) '$($labels[$j].Text)' $b")
            }
        }
    }
    foreach ($c in $parent.Controls) {
        if ($c.Controls.Count -gt 0 -and -not ($c -is [System.Windows.Forms.UpDownBase]) -and -not ($c -is [System.Windows.Forms.ComboBox]) -and -not ($c -is [System.Windows.Forms.TreeView])) {
            Test-Touching $c "$path/$($c.Name)" $problems
        }
    }
}


$fontScales = @(1.0, 1.25, 1.5, 1.75)

# Window size (issue #156). Each form page is checked at the minimum, the default and a large window
# size, in English and Traditional Chinese at 100-175 %:
#   - no overlap, clipping, touching captions or control outside a non-scrolling container that the
#     page does not already have at its default size (the window size must not add any),
#   - at the large size the tree views are wider and taller and the boxes that hold names and paths
#     are wider than at the default size; the navigation buttons keep their distance to the bottom
#     edge (and the right edge for the buttons on the right),
#   - the form opens compact (the tree 300 px wide at the designer scale); a narrower window takes
#     width from the tree first while the property area keeps its width, down to a 200 px tree,
#   - back at the default size every control is exactly where it was (nothing drifts).
$sizeChecks = @{
    'AssemblyExportForm' = @{
        'joint page' = @{ Trees = @('treeViewJointTree'); Names = @('textBoxJointName', 'comboBoxOrigin', 'comboBoxAxis'); Keep = @('textBoxJointName', 'comboBoxOrigin', 'comboBoxAxis', 'comboBoxJointType', 'textBoxJointX', 'textBoxLimitLower'); Scroller = 'panelJointContent' }
        'link page' = @{ Trees = @('treeViewLinkProperties'); Names = @('textBoxTexture'); Keep = @('groupBox5', 'groupBox4', 'textBoxTexture'); Scroller = 'panelLinkContent' }
    }
    'PartExportForm' = @{
        'form' = @{ Trees = @(); Names = @('textBox_save_as', 'textBox_texture') }
    }
}
$navigation = 'Cancel|Finish|Next|Previous|ExportUrdfOnly|button_finish|button_cancel'

function Get-AllBounds([System.Windows.Forms.Control]$parent, [string]$path, $into) {
    foreach ($c in $parent.Controls) {
        # The scroll position is not part of the layout: compare at the top-left scroll position.
        if (($c -is [System.Windows.Forms.ScrollableControl]) -and $c.AutoScroll) { $c.AutoScrollPosition = New-Object System.Drawing.Point(0, 0) }
        $into["$path/$($c.Name)"] = $c.Bounds
        if (-not ($c -is [System.Windows.Forms.UpDownBase]) -and -not ($c -is [System.Windows.Forms.ComboBox])) { Get-AllBounds $c "$path/$($c.Name)" $into }
    }
}

# Rectangle of a control in the form's client coordinates.
function Get-FormRect([System.Windows.Forms.Form]$form, [System.Windows.Forms.Control]$c) {
    return $form.RectangleToClient($c.Parent.RectangleToScreen($c.Bounds))
}

function Find-One([System.Windows.Forms.Control]$root, [string]$name) {
    return @($root.Controls.Find($name, $true)) | Select-Object -First 1
}

function Get-NavButtons([System.Windows.Forms.Control]$root) {
    $found = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack; $stack.Push($root)
    while ($stack.Count -gt 0) {
        foreach ($c in $stack.Pop().Controls) {
            if (($c -is [System.Windows.Forms.Button]) -and $c.Name -match $navigation -and (Test-Shown $c)) { [void]$found.Add($c) }
            if (Test-Shown $c) { $stack.Push($c) }
        }
    }
    return ,$found
}

# Scrolling containers shown on a page.
function Get-Scrollers([System.Windows.Forms.Control]$root) {
    $found = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack; $stack.Push($root)
    while ($stack.Count -gt 0) {
        $c = $stack.Pop()
        if (($c -is [System.Windows.Forms.ScrollableControl]) -and $c.AutoScroll -and -not ($c -is [System.Windows.Forms.Form])) { [void]$found.Add($c) }
        foreach ($child in $c.Controls) { if ((Test-Shown $child) -and -not ($child -is [System.Windows.Forms.UpDownBase])) { $stack.Push($child) } }
    }
    return ,$found
}

# A problem without the bounds it names, to compare problems at different window sizes.
function Get-ProblemKey([string]$problem) { return ($problem -replace '\{[^}]*\}', '') }

# The size the layout of a form was made for, inside the window.
$growOf = $assembly.GetType('SW2URDF.UI.GrowLayout')
$baseSize = if ($growOf) { $growOf.GetField('baseSize', $flags) }
$growOf = if ($growOf) { $growOf.GetMethod('Find', [Reflection.BindingFlags]'Static,NonPublic,Public') }

function Set-FormSize([System.Windows.Forms.Form]$form, [System.Drawing.Size]$size) {
    $form.Size = $size
    [System.Windows.Forms.Application]::DoEvents()
}

foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($language in @('en', 'chinese')) {
        $ui = [Activator]::CreateInstance($uiType, @(, [string]$language))
        foreach ($scale in $fontScales) {
            $script:baseFont = $null
            $font = Get-FormFont $ui $scale
            $form = New-Form $type $font ([single](96 * $scale)) $ui
            $label = "$($type.Name) [window size $language x$scale]"
            # A joint with a limit mate shows a four-line limit source text in the joint page header,
            # which must stay clear of the page description and the tree at every size.
            $source = Find-One $form 'labelLimitSource'
            if ($source) {
                $source.Text = [string]::Format($ui.Get('Limit mate {0} ({1})'), 'LimitMate_shoulder_pitch', $ui.Get('angle limit mate')) + "`n" +
                    [string]::Format($ui.Get('SolidWorks range {0} to {1}, reference {2}'), ("-120.5" + [char]176), ("135.25" + [char]176), ("7.375" + [char]176)) + "`n" +
                    [string]::Format($ui.Get('URDF (relative): {0} to {1} = {2} to {3}'), ("-127.875" + [char]176), ("127.875" + [char]176), '-2.23183', '2.23183') + "`n" +
                    [string]::Format($ui.Get('Direction: taken from the mate type rule ({0})'), $ui.Get('the dimension is controlled by an equation or a global variable'))
            }
            try {
                $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
                $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
                $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                $default = $form.Size
                $minimum = if ($form.MinimumSize.Width -gt 0) { $form.MinimumSize } else { New-Object System.Drawing.Size([int]($default.Width * 0.7), [int]($default.Height * 0.7)) }
                # Windows does not make a window larger than its maximum tracking size (about the screen size).
                $track = [System.Windows.Forms.SystemInformation]::MaxWindowTrackSize
                $large = New-Object System.Drawing.Size([Math]::Min([int]($default.Width * 1.5), $track.Width), [Math]::Min([int]($default.Height * 1.3), $track.Height))
                # The layout is higher than that: the whole page cannot be shown.
                $grow = if ($growOf) { $growOf.Invoke($null, @([System.Windows.Forms.Form]$form)) }
                $needed = if ($grow) { $baseSize.GetValue($grow).Height + $form.Height - $form.ClientSize.Height } else { $default.Height }
                $limited = $default.Width -ge $track.Width -or $needed -ge $track.Height
                foreach ($page in (Get-Pages $form)) {
                    $problems = New-Object System.Collections.ArrayList
                    $panel = $page[2]
                    Set-FormSize $form $default
                    if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
                    if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
                    [System.Windows.Forms.Application]::DoEvents()
                    $check = $sizeChecks[$type.Name][$page[0]]
                    # What the page already has at the default size is not caused by the window size.
                    $baseline = New-Object System.Collections.ArrayList
                    Test-Container $page[1] ($page[0]) $false $baseline
                    Test-Touching $page[1] ($page[0]) $baseline
                    $known = @{}; foreach ($p in $baseline) { $known[(Get-ProblemKey $p)] = $true }
                    $before = @{}; Get-AllBounds $page[1] '' $before
                    $client = $form.ClientSize
                    $nav = @{}
                    foreach ($b in (Get-NavButtons $page[1])) { $r = Get-FormRect $form $b; $nav[$b.Name] = @($r, ($client.Width - $r.Right), ($client.Height - $r.Bottom), ($r.Left + $r.Width / 2 -gt $client.Width / 2)) }
                    $widths = @{}
                    foreach ($n in @($check.Trees + $check.Names + $check.Keep | Where-Object { $_ })) { $c = Find-One $page[1] $n; if ($c) { $widths[$n] = $c.Size } else { [void]$problems.Add("missing control $n") } }
                    # The tree is the part that gives: the form opens with a compact tree (300 px at
                    # the designer scale), and a narrower window takes width from the tree first, while
                    # the property area keeps its width, down to a tree of 200 px; only then scroll bars.
                    # Relative to the designer font (AutoScaleDimensions 6 x 12 in the designer; WinForms overwrites the property once it has scaled the form).
                    $factor = $form.CurrentAutoScaleDimensions.Width / 6
                    $treeCompact = [int][Math]::Round(300 * $factor); $treeMin = [int][Math]::Round(200 * $factor)
                    $tree = if ($check.Trees.Count) { Find-One $page[1] $check.Trees[0] } else { $null }
                    $sizes = @(,@('large', $large))
                    if ($tree) {
                        if ($tree.Width -gt $treeCompact + 2) { [void]$problems.Add("default: $($tree.Name) opens $($tree.Width) px wide, wider than the compact $treeCompact px") }
                        $sizes += ,@('narrow', (New-Object System.Drawing.Size(($default.Width - [int](($tree.Width - $treeMin) / 2)), $default.Height)))
                    }
                    $sizes += ,@('minimum', $minimum); $sizes += ,@('default', $default)
                    foreach ($size in $sizes) {
                        Set-FormSize $form $size[1]
                        $sizeProblems = New-Object System.Collections.ArrayList
                        Test-Container $page[1] ($page[0]) $false $sizeProblems
                        Test-Touching $page[1] ($page[0]) $sizeProblems
                        foreach ($p in $sizeProblems) { if ($known[(Get-ProblemKey $p)]) { continue }; [void]$problems.Add("$($size[0]) $($form.Size.Width)x$($form.Size.Height): $p") }
                        if ($size[0] -eq 'narrow') {
                            if ($tree.Width -ge $widths[$tree.Name].Width) { [void]$problems.Add("narrow: $($tree.Name) does not give up width: $($widths[$tree.Name].Width) -> $($tree.Width)") }
                            foreach ($n in $check.Keep) {
                                $c = Find-One $page[1] $n
                                if ($c -and $c.Width -ne $widths[$n].Width) { [void]$problems.Add("narrow: $n changes width $($widths[$n].Width) -> $($c.Width) before the tree reaches its minimum") }
                            }
                            $scroller = Find-One $form $check.Scroller
                            if ($scroller -and $scroller.HorizontalScroll.Visible) { [void]$problems.Add("narrow: $($check.Scroller) scrolls sideways while the tree is still $($tree.Width) px wide") }
                        }
                        if ($size[0] -eq 'minimum' -and $tree -and $tree.Width -lt $treeMin - 1) { [void]$problems.Add("minimum: $($tree.Name) is $($tree.Width) px, narrower than $treeMin px") }
                        # At the default size the whole page is visible without scrolling (the footer
                        # notes included).
                        if ($size[0] -eq 'default' -and $limited) {
                            [void]$skipped.Add("$label $($page[0]) : default without scrolling: needs a window $needed high, this screen allows $($track.Height)")
                        }
                        if ($size[0] -eq 'default' -and -not $limited) {
                            foreach ($scroller in (Get-Scrollers $page[1])) {
                                if ($scroller.VerticalScroll.Visible -or $scroller.HorizontalScroll.Visible) {
                                    [void]$problems.Add("default: $($scroller.Name) shows a scroll bar (content $($scroller.DisplayRectangle.Size) in $($scroller.ClientSize))")
                                }
                                foreach ($c in $scroller.Controls) {
                                    if (-not (Test-Shown $c)) { continue }
                                    $b = Get-Box $c
                                    if ($b.Bottom -gt $scroller.ClientSize.Height + 1 -or $b.Right -gt $scroller.ClientSize.Width + 1) {
                                        [void]$problems.Add("default: $($c.Name) needs scrolling in $($scroller.Name): $b in $($scroller.ClientSize)")
                                    }
                                }
                            }
                        }
                        $grow = if ($growOf) { $growOf.Invoke($null, @([System.Windows.Forms.Form]$form)) }
                        $needs = if ($grow) { $baseSize.GetValue($grow) } else { $client }
                        $roomy = $form.ClientSize.Width -gt $needs.Width -and $form.ClientSize.Height -gt $needs.Height
                        if ($size[0] -eq "large" -and -not $roomy) {
                            [void]$skipped.Add("$label $($page[0]) : large: needs a window with more than $($needs.Width)x$($needs.Height) inside, this screen allows $($form.ClientSize.Width)x$($form.ClientSize.Height)")
                        }
                        if ($size[0] -eq "large" -and $roomy) {
                            foreach ($n in $check.Trees) {
                                $c = Find-One $page[1] $n
                                if ($c -and ($c.Width -le $widths[$n].Width -or $c.Height -le $widths[$n].Height)) { [void]$problems.Add("large: tree $n does not grow: $($widths[$n]) -> $($c.Size)") }
                            }
                            foreach ($n in $check.Names) {
                                $c = Find-One $page[1] $n
                                if ($c -and $c.Width -le $widths[$n].Width) { [void]$problems.Add("large: $n does not widen: $($widths[$n].Width) -> $($c.Width)") }
                            }
                            $now = $form.ClientSize
                            foreach ($b in (Get-NavButtons $page[1])) {
                                $was = $nav[$b.Name]; if (-not $was) { continue }
                                $r = Get-FormRect $form $b
                                if ([Math]::Abs(($now.Height - $r.Bottom) - $was[2]) -gt 1) { [void]$problems.Add("large: $($b.Name) does not stay at the bottom edge") }
                                if ($was[3] -and [Math]::Abs(($now.Width - $r.Right) - $was[1]) -gt 1) { [void]$problems.Add("large: $($b.Name) does not stay at the right edge") }
                            }
                        }
                        if ($ScreenshotDirectory) {
                            $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
                            $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)))
                            $name = ("{0}_size_{1}_{2}_{3}_{4}" -f $type.Name, $language, $scale, $size[0], ($page[0] -replace ' ', ''))
                            $bmp.Save((Join-Path $ScreenshotDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                            $bmp.Dispose()
                        }
                    }
                    $after = @{}; Get-AllBounds $page[1] '' $after
                    foreach ($key in $before.Keys) {
                        if ($after[$key] -ne $before[$key]) { [void]$problems.Add("default size again: $key moved from $($before[$key]) to $($after[$key])") }
                    }
                    foreach ($p in $problems) { [void]$report.Add("$label $($page[0]) : $p") }
                    $total += $problems.Count
                }
            } finally { $form.Dispose(); $font.Dispose() }
        }
    }
}

# Small screens (issue #267). The work area is set for the test: a 1920 x 1080 screen with its taskbar
# at 100-175 %, and a 1366 x 768 one. Both forms, in both languages, then
#   - open no larger than the work area, with a minimum size that fits it, and inside it,
#   - show the navigation buttons of every page and let every other control be scrolled into view,
#   - do not scroll sideways while the tree can still give up width,
#   - have every control where it was after the window was made smaller and back, scrolled or not,
#   - use the same room as on a larger screen once the window has it: no scroll bar is left.
function Get-Shown([System.Windows.Forms.Control]$parent) {
    foreach ($c in $parent.Controls) {
        if (-not (Test-Shown $c)) { continue }
        $c
        if ($c.Controls.Count -and -not ($c -is [System.Windows.Forms.UpDownBase]) -and -not ($c -is [System.Windows.Forms.ComboBox])) { Get-Shown $c }
    }
}

function Test-Reachable($form, [System.Windows.Forms.Control]$page, [string]$state, $problems) {
    $client = New-Object System.Drawing.Rectangle([System.Drawing.Point]::Empty, $form.ClientSize)
    foreach ($c in @(Get-Shown $page)) {
        if ($c.Width -le 0 -or $c.Height -le 0) { continue }
        $scroller = $c.Parent
        while ($scroller -and $scroller -ne $form -and -not (($scroller -is [System.Windows.Forms.ScrollableControl]) -and $scroller.AutoScroll)) { $scroller = $scroller.Parent }
        if (-not $scroller -or $scroller -eq $form) {
            $r = Get-FormRect $form $c
            $view = $client
        } else {
            $scroller.ScrollControlIntoView($c)
            $r = Get-FormRect $form $c
            $view = [System.Drawing.Rectangle]::Intersect($client, $form.RectangleToClient($scroller.RectangleToScreen($scroller.ClientRectangle)))
            # A control larger than the panel is shown in parts; of a group, its controls are checked.
            $r.Width = [Math]::Min($r.Width, $view.Width); $r.Height = [Math]::Min($r.Height, $view.Height)
            if ($c.Controls.Count) { continue }
        }
        $view.Inflate(1, 1)
        if (-not $view.Contains($r)) { [void]$problems.Add("${state}: $($c.Name) cannot be reached: $r in $view") }
    }
    foreach ($scroller in (Get-Scrollers $page)) { $scroller.AutoScrollPosition = New-Object System.Drawing.Point(0, 0) }
}

$smallScreens = @(@(1920, 1032, 1.0), @(1920, 1020, 1.25), @(1920, 1008, 1.5), @(1920, 996, 1.75), @(1366, 728, 1.0))
if (-not $areaField) {
    [void]$report.Add('small screen : the forms cannot be laid out for a work area of the test (UiLayout.TestWorkingArea is missing)'); $total++
    $smallScreens = @()
}
$fitToWindow = $assembly.GetType('SW2URDF.UI.UiLayout', $true).GetMethod('FitButtonHeightsToWindow', [Reflection.BindingFlags]'Static,NonPublic,Public')
foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($language in @('en', 'chinese')) {
        $ui = [Activator]::CreateInstance($uiType, @(, [string]$language))
        foreach ($screen in $smallScreens) {
            $scale = $screen[2]
            # Off every real screen, like the windows of the other checks.
            $area = New-Object System.Drawing.Rectangle(-30000, -30000, $screen[0], $screen[1])
            $script:baseFont = $null
            $font = Get-FormFont $ui $scale
            $areaField.SetValue($null, $area)
            $form = New-Form $type $font ([single](96 * $scale)) $ui
            $label = "$($type.Name) [small screen $($screen[0])x$($screen[1]) $language x$scale]"
            $problems = New-Object System.Collections.ArrayList
            try {
                $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(($area.Left + 600), ($area.Top + 300))
                $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
                $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                [void]$fitToWindow.Invoke($null, @([System.Windows.Forms.Control]$form)); [System.Windows.Forms.Application]::DoEvents()
                $default = $form.Size
                if ($default.Width -gt $area.Width -or $default.Height -gt $area.Height) { [void]$problems.Add("opens at $default, larger than the work area $($area.Size)") }
                elseif (-not $area.Contains($form.Bounds)) { [void]$problems.Add("opens at $($form.Bounds), not inside the work area $area") }
                if ($form.MinimumSize.Width -gt $area.Width -or $form.MinimumSize.Height -gt $area.Height) { [void]$problems.Add("minimum size $($form.MinimumSize) is larger than the work area $($area.Size)") }
                foreach ($page in (Get-Pages $form)) {
                    $panel = $page[2]
                    Set-FormSize $form $default
                    if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
                    if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
                    [System.Windows.Forms.Application]::DoEvents()
                    $check = $sizeChecks[$type.Name][$page[0]]
                    $factor = $form.CurrentAutoScaleDimensions.Width / 6
                    $tree = if ($check.Trees.Count) { Find-One $page[1] $check.Trees[0] } else { $null }
                    $before = @{}; Get-AllBounds $page[1] '' $before
                    $minimum = if ($form.MinimumSize.Width -gt 0) { $form.MinimumSize } else { New-Object System.Drawing.Size([int]($default.Width * 0.7), [int]($default.Height * 0.7)) }
                    $lower = New-Object System.Drawing.Size($default.Width, [Math]::Max($minimum.Height, [int]($default.Height * 0.8)))
                    foreach ($size in @(@('default', $default), @('lower', $lower), @('minimum', $minimum))) {
                        Set-FormSize $form $size[1]
                        Test-Reachable $form $page[1] "$($page[0]) $($size[0])" $problems
                        if ($size[0] -eq 'minimum') { continue }
                        foreach ($scroller in (Get-Scrollers $page[1])) {
                            if ($scroller.HorizontalScroll.Visible -and (-not $tree -or $tree.Width -gt 200 * $factor + 2)) {
                                $width = if ($tree) { ", the tree is $($tree.Width) px wide" }
                                [void]$problems.Add("$($page[0]) $($size[0]): $($scroller.Name) scrolls sideways (content $($scroller.DisplayRectangle.Size) in $($scroller.ClientSize))$width")
                            }
                        }
                    }
                    # Resized while scrolled down: at the default size again nothing has moved.
                    foreach ($scroller in (Get-Scrollers $page[1])) { $scroller.AutoScrollPosition = New-Object System.Drawing.Point(0, 10000) }
                    Set-FormSize $form $default
                    $after = @{}; Get-AllBounds $page[1] '' $after
                    foreach ($key in $before.Keys) {
                        if ($after[$key] -ne $before[$key]) { [void]$problems.Add("$($page[0]) default size again: $key moved from $($before[$key]) to $($after[$key])") }
                    }
                    # With the room the layout was made for, no scroll bar is left.
                    $grow = if ($growOf) { $growOf.Invoke($null, @([System.Windows.Forms.Form]$form)) }
                    if ($grow) {
                        $base = $baseSize.GetValue($grow)
                        $want = New-Object System.Drawing.Size(($base.Width + $form.Width - $form.ClientSize.Width), ($base.Height + $form.Height - $form.ClientSize.Height + 20))
                        $track = [System.Windows.Forms.SystemInformation]::MaxWindowTrackSize
                        if ($want.Width -gt $track.Width -or $want.Height -gt $track.Height) {
                            [void]$skipped.Add("$label $($page[0]) : all of the room: needs a window of $($want.Width)x$($want.Height), this screen allows $($track.Width)x$($track.Height)")
                        } else {
                            Set-FormSize $form $want
                            foreach ($scroller in (Get-Scrollers $page[1])) {
                                if ($scroller.VerticalScroll.Visible -or $scroller.HorizontalScroll.Visible) { [void]$problems.Add("$($page[0]) with all of the room: $($scroller.Name) shows a scroll bar (content $($scroller.DisplayRectangle.Size) in $($scroller.ClientSize))") }
                            }
                        }
                    }
                }
            } finally { $areaField.SetValue($null, $null); $form.Dispose(); $font.Dispose() }
            foreach ($p in $problems) { [void]$report.Add("$label $p") }
            $total += $problems.Count
        }
    }
}

$report | ForEach-Object { Write-Output $_ }
$skipped | ForEach-Object { Write-Output "SKIPPED $_" }
Write-Output "Skipped checks (the screen is too small for them): $($skipped.Count)"
Write-Output "Layout problems: $total"
if ($total -gt 0) { exit 1 }
