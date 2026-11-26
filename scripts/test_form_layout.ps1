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

# Form font (issue #157). The checks above hand the form a font; the real constructors let the form
# choose its own font for the UI language (UiText.CreateFormFont, set before InitializeComponent).
# Build the forms that way, in English and Traditional Chinese, and check that
#   - every control that shows text uses the form's font family and character set (no control
#     falls back to another face, which in Chinese mixes serif and sans-serif glyphs),
#   - both languages use Segoe UI 8.5 pt (Windows draws Chinese glyphs through font linking),
#   - the joint page header has room for a four-line limit source text above the parent link row,
#   - the layout has no overlap, clipping or touching captions at 100-175 %, both when the form is
#     built at that scale and when an open form is moved to a monitor with that scale
#     (UiLayout.ScaleForDpi, what WM_DPICHANGED does in a per-monitor DPI aware host).
# Without CreateFormFont the form keeps the default font, which is what the forms did before.
$uiType = $assembly.GetType('SW2URDF.UI.UiText', $true)
$layoutType = $assembly.GetType('SW2URDF.UI.UiLayout')
$formFamily = 'Segoe UI'; $formSize = 8.5
$createFont = $uiType.GetMethod('CreateFormFont', $flags)
$scaleForDpi = if ($layoutType) { $layoutType.GetMethod('ScaleForDpi', [Reflection.BindingFlags]'Static,NonPublic,Public') }

function Get-FormFont($ui, [double]$scale) {
    $base = if ($createFont) { $createFont.Invoke($ui, @()) } else { [System.Windows.Forms.Control]::DefaultFont }
    return New-Object System.Drawing.Font($base.FontFamily, [single]($base.SizeInPoints * $scale), $base.Style, [System.Drawing.GraphicsUnit]::Point, $base.GdiCharSet)
}

function Test-FormFonts([System.Windows.Forms.Form]$form, [string]$language, $problems) {
    $family = $form.Font.FontFamily.GetName(1033)
    $charset = $form.Font.GdiCharSet
    if ($family -ne $formFamily) { [void]$problems.Add("form font: $family, expected $formFamily") }
    $stack = New-Object System.Collections.Stack; $stack.Push($form)
    while ($stack.Count -gt 0) {
        $parent = $stack.Pop()
        foreach ($c in $parent.Controls) {
            $shows = $c -is [System.Windows.Forms.TextBoxBase] -or $c -is [System.Windows.Forms.ComboBox] -or
                $c -is [System.Windows.Forms.UpDownBase] -or $c -is [System.Windows.Forms.TreeView] -or
                (-not [string]::IsNullOrEmpty($c.Text) -and ($c -is [System.Windows.Forms.Label] -or
                    $c -is [System.Windows.Forms.ButtonBase] -or $c -is [System.Windows.Forms.GroupBox]))
            if ($shows -and ($c.Font.FontFamily.GetName(1033) -ne $family -or $c.Font.GdiCharSet -ne $charset)) {
                [void]$problems.Add("font: $($c.Name) $($c.Font.FontFamily.GetName(1033)) charset $($c.Font.GdiCharSet) vs form $family charset $charset")
            }
            if (-not ($c -is [System.Windows.Forms.UpDownBase]) -and -not ($c -is [System.Windows.Forms.ComboBox])) { $stack.Push($c) }
        }
    }
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
foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($language in @('en', 'chinese')) {
        $ui = [Activator]::CreateInstance($uiType, @(, [string]$language))
        foreach ($scale in $fontScales) {
            foreach ($how in @('built', 'moved')) {
                if ($how -eq 'moved' -and $scale -eq 1.0) { continue }
                $script:baseFont = $null
                if ($how -eq 'built') {
                    $font = Get-FormFont $ui $scale
                    $form = New-Form $type $font ([single](96 * $scale)) $ui
                } else {
                    # Built for a 100 % monitor, then moved to one with this scale.
                    $font = Get-FormFont $ui 1.0
                    $form = New-Form $type $font ([single]96) $ui
                }
                $label = "$($type.Name) [form font $language x$scale $how]"
                if ($how -eq 'built' -and $scale -eq 1.0) {
                    $made = $createFont.Invoke($ui, @())
                    if ($made.FontFamily.GetName(1033) -ne $formFamily -or [Math]::Abs($made.SizeInPoints - $formSize) -gt 0.01) { [void]$report.Add("$label : CreateFormFont gives $($made.FontFamily.GetName(1033)) $($made.SizeInPoints)pt, expected $formFamily $($formSize)pt"); $total++ }
                }
                # A joint with a limit mate shows a four-line limit source text in the joint page header.
                $source = @($form.Controls.Find('labelLimitSource', $true)) | Select-Object -First 1
                if ($source) {
                    $source.Text = [string]::Format($ui.Get('Limit mate {0} ({1})'), 'LimitAngle1', $ui.Get('angle limit mate')) + "`n" +
                        [string]::Format($ui.Get('SolidWorks range {0} to {1}, reference {2}'), '60 deg', '180 deg', '90 deg') + "`n" +
                        [string]::Format($ui.Get('URDF (relative): {0} to {1} = {2} to {3}'), '-90 deg', '30 deg', '-1.5708 rad', '0.5236 rad') + "`n" +
                        $ui.Get('Direction: measured on the model')
                }
                # The parent and child link names are shown next to their captions, on the same line.
                foreach ($pair in @(@('label64', 'labelParent', 'base_link'), @('label65', 'labelChild', 'shoulder_upper_arm_link'))) {
                    $value = @($form.Controls.Find($pair[1], $true)) | Select-Object -First 1
                    if ($value) { $value.Text = $pair[2] }
                }
                try {
                    $problems = New-Object System.Collections.ArrayList
                    foreach ($pair in @(@('label64', 'labelParent'), @('label65', 'labelChild'))) {
                        $caption = @($form.Controls.Find($pair[0], $true)) | Select-Object -First 1; $value = @($form.Controls.Find($pair[1], $true)) | Select-Object -First 1
                        if ($caption -and $value -and $type.Name -eq 'AssemblyExportForm') {
                            if ($value.Left -lt $caption.Right) { [void]$problems.Add("$($pair[1]) starts at $($value.Left), inside its caption $($pair[0]) ending at $($caption.Right)") }
                            if ([Math]::Abs(($value.Top + $value.Height / 2) - ($caption.Top + $caption.Height / 2)) -gt 2) { [void]$problems.Add("$($pair[1]) is not on the line of its caption $($pair[0]): $($value.Bounds) vs $($caption.Bounds)") }
                        }
                    }
                    if ($how -eq 'moved') {
                        if (-not $scaleForDpi) { [void]$problems.Add('no per-monitor DPI rescale: UiLayout.ScaleForDpi is missing') }
                        else {
                            $before = $form.Font.SizeInPoints
                            [void]$scaleForDpi.Invoke($null, @($form, [single]96, [single](96 * $scale)))
                            if ([Math]::Abs($form.Font.SizeInPoints - $before * $scale) -gt 0.05) {
                                [void]$problems.Add("form font is $($form.Font.SizeInPoints)pt after the move, expected $($before * $scale)pt")
                            }
                        }
                    }
                    Test-FormFonts $form $language $problems
                    $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
                    $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
                    $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                    foreach ($page in (Get-Pages $form)) {
                        $panel = $page[2]
                        if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
                        if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
                        [System.Windows.Forms.Application]::DoEvents()
                        $pageProblems = New-Object System.Collections.ArrayList
                        Test-Container $page[1] ($page[0]) $false $pageProblems
                        Test-Touching $page[1] ($page[0]) $pageProblems
                        foreach ($p in $pageProblems) { [void]$problems.Add($p) }
                        if ($ScreenshotDirectory) {
                            $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
                            $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)))
                            $name = ("{0}_formfont_{1}_{2}_{3}_{4}" -f $type.Name, $language, $scale, $how, ($page[0] -replace ' ', ''))
                            $bmp.Save((Join-Path $ScreenshotDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                            $bmp.Dispose()
                        }
                    }
                    foreach ($p in $problems) { [void]$report.Add("$label : $p") }
                    $total += $problems.Count
                } finally { $form.Dispose(); $font.Dispose() }
            }
        }
    }
}

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

# A problem without the bounds and captions it names, to compare problems at different window sizes
# and before and after the angle unit is chosen (the unit changes the captions).
function Get-ProblemKey([string]$problem) { return ($problem -replace '\{[^}]*\}', '' -replace "'[^']*'", '') }

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
                        # notes included), unless the screen is too small for the form.
                        $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
                        if ($size[0] -eq 'default' -and $form.Height -lt $area.Height) {
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
                        if ($size[0] -eq "large" -and $large.Width -gt $default.Width -and $large.Height -gt $default.Height) {
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

# The limit source text shares the header of the joint page: a long one, as a limit mate shows.
function Set-LongLimitText([System.Windows.Forms.Form]$target, $ui) {
    $source = Find-One $target 'labelLimitSource'
    if ($source) {
        $source.Text = [string]::Format($ui.Get('Limit mate {0} ({1})'), 'LimitMate_shoulder_pitch', $ui.Get('angle limit mate')) + "`n" +
            [string]::Format($ui.Get('SolidWorks range {0} to {1}, reference {2}'), ("-120.5" + [char]176), ("135.25" + [char]176), ("7.375" + [char]176)) + "`n" +
            [string]::Format($ui.Get('URDF (relative): {0} to {1} = {2} to {3}'), ("-127.875" + [char]176), ("127.875" + [char]176), '-2.23183', '2.23183') + "`n" +
            [string]::Format($ui.Get('Direction: taken from the mate type rule ({0})'), $ui.Get('the dimension is controlled by an equation or a global variable'))
    }
    # What the constructor does when the text changes size.
    $align = $target.GetType().GetMethod('AlignFieldLabels', $flags)
    if ($align) { [void]$align.Invoke($target, @()) }
}

# Pages are compared from the top of their content.
function Reset-Scrolling([System.Windows.Forms.Control]$root) {
    foreach ($c in $root.Controls) {
        if (($c -is [System.Windows.Forms.ScrollableControl]) -and $c.AutoScroll) { $c.AutoScrollPosition = New-Object System.Drawing.Point(0, 0) }
        if (-not ($c -is [System.Windows.Forms.UpDownBase])) { Reset-Scrolling $c }
    }
    [System.Windows.Forms.Application]::DoEvents()
}

# Problems of each page of a form, without bounds, keyed by page.
function Get-PageProblems([System.Windows.Forms.Form]$form) {
    $found = @{}
    $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
    $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
    $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
    foreach ($page in (Get-Pages $form)) {
        $panel = $page[2]
        if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
        if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
        [System.Windows.Forms.Application]::DoEvents()
        Reset-Scrolling $page[1]
        $pageProblems = New-Object System.Collections.ArrayList
        Test-Container $page[1] ($page[0]) $false $pageProblems
        Test-Touching $page[1] ($page[0]) $pageProblems
        foreach ($p in $pageProblems) { $found["$($page[0]): $(Get-ProblemKey $p)"] = $true }
    }
    return $found
}

# Angle unit (issue #155). The assembly form has an angle unit choice on the joint page and on the
# link page. With degrees chosen and the captions of a revolute joint, in English and Traditional
# Chinese at 100-175 %: the angle captions name the unit (deg / deg/s, or rad / rad/s), the linear
# captions keep theirs, and choosing the unit adds no overlap, clipping or touching captions to the
# pages (what a page already has with the designer captions is left to the other checks, except
# anything that involves the angle unit choice or the limit source text, which ends before it).
$angleType = $assembly.GetType('SW2URDF.UI.AngleUnit')
$angleCaptions = @{
    'Degrees' = @{ label6 = '(deg)'; labelLowerLimit = '(deg)'; labelLimitUpper = '(deg)'; labelVelocity = '(deg/s)'; labelSoftLower = '(deg)'; labelSoftUpper = '(deg)'; label36 = 'deg'; label23 = 'deg'; labelEffort = '(N'; label3 = '(m)' }
    'Radians' = @{ label6 = '(rad)'; labelLowerLimit = '(rad)'; labelLimitUpper = '(rad)'; labelVelocity = '(rad/s)'; labelSoftLower = '(rad)'; labelSoftUpper = '(rad)'; label36 = 'rad'; label23 = 'rad'; labelEffort = '(N'; label3 = '(m)' }
}
$type = $assembly.GetType('SW2URDF.UI.AssemblyExportForm', $true)
$setUnit = $type.GetMethod('SetAngleUnit', $flags)
$setCaptions = $type.GetMethod('SetJointCaptions', $flags)
foreach ($language in @('en', 'chinese')) {
    $ui = [Activator]::CreateInstance($uiType, @(, [string]$language))
    foreach ($scale in $fontScales) {
        foreach ($unit in @('Degrees', 'Radians')) {
            $script:baseFont = $null
            $font = Get-FormFont $ui $scale
            $script:jointChoice = $null
            $form = New-Form $type $font ([single](96 * $scale)) $ui
            $label = "$($type.Name) [angle unit $unit $language x$scale]"
            $problems = New-Object System.Collections.ArrayList
            try {
                $known = @{}
                if ($setUnit) {
                    $probe = New-Form $type $font ([single](96 * $scale)) $ui
                    Set-LongLimitText $probe $ui
                    try { $known = Get-PageProblems $probe } finally { $probe.Dispose() }
                }
                if (-not $angleType -or -not $setUnit -or -not $setCaptions) {
                    [void]$problems.Add('no angle unit choice: AngleUnit, SetAngleUnit or SetJointCaptions is missing')
                } else {
                    [void]$setUnit.Invoke($form, @([Enum]::Parse($angleType, $unit)))
                    [void]$setCaptions.Invoke($form, @('revolute'))
                    Set-LongLimitText $form $ui
                    foreach ($name in $angleCaptions[$unit].Keys) {
                        $c = Find-One $form $name
                        if (-not $c) { [void]$problems.Add("missing caption $name"); continue }
                        if (-not $c.Text.Contains($angleCaptions[$unit][$name])) { [void]$problems.Add("caption $name '$($c.Text)' does not show '$($angleCaptions[$unit][$name])'") }
                    }
                }
                foreach ($name in @('comboBoxAngleUnitJoint', 'comboBoxAngleUnitLink')) {
                    $c = Find-One $form $name
                    if (-not $c) { [void]$problems.Add("missing angle unit choice $name") }
                    elseif (-not ($c -is [System.Windows.Forms.ComboBox]) -or $c.Items.Count -ne 2) { [void]$problems.Add("$name does not offer two units") }
                }
                $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
                $wasVisible = @{}; foreach ($c in $form.Controls) { $wasVisible[$c.Name] = (Test-Shown $c) }
                $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                foreach ($page in (Get-Pages $form)) {
                    $panel = $page[2]
                    if ($page[0] -eq 'joint page') { $panel.Visible = $false; foreach ($c in $form.Controls) { if ($c -ne $panel -and $wasVisible[$c.Name]) { $c.Visible = $true } } }
                    if ($page[0] -eq 'link page') { $panel.Visible = $true; foreach ($c in $form.Controls) { if ($c -ne $panel) { $c.Visible = $false } } }
                    [System.Windows.Forms.Application]::DoEvents()
                    Reset-Scrolling $page[1]
                    $pageProblems = New-Object System.Collections.ArrayList
                    Test-Container $page[1] ($page[0]) $false $pageProblems
                    Test-Touching $page[1] ($page[0]) $pageProblems
                    # The choice is a form-level setting in the page header: above the tree and the
                    # properties, at the right end of the property area, at the same place on both pages.
                    $which = if ($page[0] -eq 'joint page') { @('comboBoxAngleUnitJoint', 'labelAngleUnitJoint', 'treeViewJointTree', 'comboBoxOrigin') } else { @('comboBoxAngleUnitLink', 'labelAngleUnitLink', 'treeViewLinkProperties', 'groupBox5') }
                    $choice = Find-One $form $which[0]; $caption = Find-One $form $which[1]; $tree = Find-One $form $which[2]; $edge = Find-One $form $which[3]
                    if ($choice -and $caption -and $tree -and $edge) {
                        $rc = Get-FormRect $form $choice; $rl = Get-FormRect $form $caption; $rt = Get-FormRect $form $tree; $re = Get-FormRect $form $edge
                        if ($rc.Bottom -gt $rt.Top -or $rl.Bottom -gt $rt.Top) { [void]$pageProblems.Add("angle unit choice is not in the header band: $rc / $rl below the top of $($tree.Name) $($rt.Top)") }
                        if ($rc.Right -lt $re.Right - 2) { [void]$pageProblems.Add("angle unit choice is not at the right: $rc ends left of $($edge.Name) $($re.Right)") }
                        if ($rl.Right -gt $rc.Left) { [void]$pageProblems.Add("angle unit caption $rl is not left of its box $rc") }
                        if ($page[0] -eq 'joint page') { $script:jointChoice = $rc } elseif ($script:jointChoice -and ([Math]::Abs($rc.X - $script:jointChoice.X) -gt 1 -or [Math]::Abs($rc.Y - $script:jointChoice.Y) -gt 1)) {
                            [void]$pageProblems.Add("angle unit choice is at $rc on the link page but at $($script:jointChoice) on the joint page")
                        }
                    }
                    foreach ($p in $pageProblems) {
                        if ($p -notmatch 'AngleUnit|labelLimitSource' -and $known["$($page[0]): $(Get-ProblemKey $p)"]) { continue }
                        [void]$problems.Add("$($page[0]): $p")
                    }
                    if ($ScreenshotDirectory) {
                        $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
                        $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)))
                        $name = ("{0}_angles_{1}_{2}_{3}_{4}" -f $type.Name, $unit, $language, $scale, ($page[0] -replace ' ', ''))
                        $bmp.Save((Join-Path $ScreenshotDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                        $bmp.Dispose()
                    }
                }
            } finally { $form.Dispose(); $font.Dispose() }
            foreach ($p in $problems) { [void]$report.Add("$label : $p") }
            $total += $problems.Count
        }
    }
}


# The push buttons in the bottom row of a page share their top and height (issue #174): at every
# scaling, with the captions of both languages, also after the window is resized and after a DPI
# change. Page tops differ, so each page is checked on its own.
function Test-ButtonRow($form, [string]$page, [string]$label, $problems) {
    $rows = @{}
    $stack = New-Object System.Collections.Stack; $stack.Push($form)
    while ($stack.Count -gt 0) {
        foreach ($c in $stack.Pop().Controls) {
            if (-not $c.Visible) { continue }
            if (($c -is [System.Windows.Forms.Button]) -and $c.Name -match 'Cancel|Finish|Next|Previous|ExportUrdfOnly') {
                $rows[$c.Name] = $c.Parent.RectangleToScreen($c.Bounds)
            }
            $stack.Push($c)
        }
    }
    $tops = @($rows.Values | ForEach-Object { $_.Top } | Sort-Object -Unique)
    $heights = @($rows.Values | ForEach-Object { $_.Height } | Sort-Object -Unique)
    if ($rows.Count -lt 2) { [void]$problems.Add("${label} : button row ${page}: only $($rows.Count) button(s) found") }
    if ($tops.Count -gt 1 -or $heights.Count -gt 1) {
        $text = ($rows.Keys | Sort-Object | ForEach-Object { "$_ top $($rows[$_].Top - $form.Top) height $($rows[$_].Height)" }) -join ', '
        [void]$problems.Add("${label} : button row ${page}: $text")
    }
}

$layout = $assembly.GetType('SW2URDF.UI.UiLayout')
$uiTextType = $assembly.GetType('SW2URDF.UI.UiText', $true)
$fitForm = $layout.GetMethod('FitForm', [Reflection.BindingFlags]'Static,NonPublic,Public')
foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($language in 'english', 'chinese') {
        foreach ($f in $fonts) {
            foreach ($scale in @(1.0, 1.25, 1.5, 1.75)) {
                $font = New-Object System.Drawing.Font($f[0], [single]($f[1] * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
                $form = New-Form $type $font ([single](96 * $scale))
                $fonts2 = @($font)
# Boxes that show a number: every single-line text box and spin box except those holding a name or a path.
$textBoxNames = 'textBoxJointName', 'textBoxTexture', 'textBox_texture', 'textBox_save_as'
function Get-NumberBoxes([System.Windows.Forms.Control]$parent) {
    foreach ($c in $parent.Controls) {
        if ($c -is [System.Windows.Forms.UpDownBase]) { $c }
        elseif ($c -is [System.Windows.Forms.TextBox]) { if (-not $c.Multiline -and $textBoxNames -notcontains $c.Name) { $c } }
        else { Get-NumberBoxes $c }
    }
}

# What a box draws, as the pixels inside its border, from column left on.
function Get-BoxPixels([System.Windows.Forms.TextBox]$edit, [int]$left = 3) {
    $bmp = New-Object System.Drawing.Bitmap($edit.Width, $edit.Height)
    $edit.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)))
    $part = [System.Drawing.Rectangle]::FromLTRB($left, 3, $bmp.Width - 3, $bmp.Height - 3)
    $data = $bmp.LockBits($part, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $row = 3 * $data.Width
    $bytes = New-Object byte[] ($row * $data.Height)
    for ($y = 0; $y -lt $data.Height; $y++) {
        [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $bytes, $y * $row, $row)
    }
    $bmp.UnlockBits($data); $bmp.Dispose()
    return [Convert]::ToBase64String($bytes)
}

# A value with an exponent shows its exponent in every number box (issue #173). The box may show fewer
# digits, but its text stays the whole value, a tool tip has the whole value, and what is drawn is the
# shown text in full.
function Test-NumberBox([System.Windows.Forms.Control]$box, [string]$value, [string]$label, $problems) {
    $edit = $box
    if ($box -is [System.Windows.Forms.UpDownBase]) { $edit = @($box.Controls | Where-Object { $_ -is [System.Windows.Forms.TextBox] })[0] }
    $name = "${label} : number box $($box.Name):"
    $old = $box.Text
    $box.Text = $value
    $shown = if ($shownText) { [string]$shownText.Invoke($null, @([System.Windows.Forms.TextBox]$edit)) } else { $value }
    # The shown text fits: in the wide box it does not go on past the width of this one.
    $wideBox.Font = $edit.Font
    $end = 2 + $edit.ClientSize.Width
    $wideBox.Text = $shown; $past = Get-BoxPixels $wideBox $end
    $wideBox.Text = ''; $blank = Get-BoxPixels $wideBox $end
    if ($past -cne $blank) { [void]$problems.Add("$name '$shown' does not fit in $($edit.ClientSize.Width) px") }
    $exponent = [regex]::Match($value, 'E[-+]\d+$').Value
    if ($exponent -and -not $shown.EndsWith($exponent)) { [void]$problems.Add("$name '$shown' lacks the exponent of $value") }
    if ($shown -cne $value) {
        # Shown with fewer digits: still the same number, rounded to the digits shown.
        $digits = [regex]::Match($shown, '^-?0*\.?0*([\d.]*)').Groups[1].Value.Replace('.', '').Length
        $number = [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
        $rounded = [double]::Parse($shown, [Globalization.CultureInfo]::InvariantCulture)
        if ([Math]::Abs($rounded - $number) -gt [Math]::Abs($number) * 5.01 * [Math]::Pow(10, -[Math]::Max(1, $digits))) { [void]$problems.Add("$name '$shown' is not $value rounded") }
        $tip = if ($fullValueTip) { $fullValueTip.Invoke($null, @([System.Windows.Forms.Control]$box)) } else { $null }
        if ($tip -cne $value) { [void]$problems.Add("$name tool tip '$tip' is not the value $value") }
        # Drawn like a box that holds just the shown text, also while disabled.
        foreach ($enabled in $true, $false) {
            $box.Enabled = $enabled
            $drawn = Get-BoxPixels $edit
            $box.Text = $shown; $expected = Get-BoxPixels $edit
            $box.Text = $value
            if ($drawn -cne $expected) { [void]$problems.Add("$name does not draw '$shown' for $value (enabled: $enabled)") }
        }
        $box.Enabled = $true
    }
    if ($box.Text -cne $value) { [void]$problems.Add("$name its text became '$($box.Text)' instead of $value") }
    $box.Text = $old
}

$static = [Reflection.BindingFlags]'Static,NonPublic,Public'
$layout = $assembly.GetType('SW2URDF.UI.UiLayout', $true)
$uiTextType = $assembly.GetType('SW2URDF.UI.UiText', $true)
$fitForm = $layout.GetMethod('FitForm', $static)
$fullValueTip = $layout.GetMethod('FullValueTip', $static)
$display = $assembly.GetType('SW2URDF.UI.NumberBoxDisplay')
$shownText = if ($display) { $display.GetMethod('ShownText', $static) }
# A plain box, wide enough for every value, to see how wide a text is drawn.
$wideForm = New-Object System.Windows.Forms.Form
$wideForm.StartPosition = 'Manual'; $wideForm.Location = New-Object System.Drawing.Point(-32000, -32000)
$wideForm.ShowInTaskbar = $false; $wideForm.Width = 800
$wideBox = New-Object System.Windows.Forms.TextBox; $wideBox.Width = 600
$wideForm.Controls.Add($wideBox); $wideForm.Show(); $wideForm.ActiveControl = $null
foreach ($typeName in $formTypes) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($language in 'english', 'chinese') {
        foreach ($f in @($fonts) + ,@('Segoe UI', 8.5)) {
            foreach ($scale in @(1.0, 1.25, 1.5, 1.75)) {
                $font = New-Object System.Drawing.Font($f[0], [single]($f[1] * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
                $form = New-Form $type $font ([single](96 * $scale))
                try {
                    $ui = [Activator]::CreateInstance($uiTextType, @(, [string]$language))
                    $ui.Apply($form)
                    [void]$fitForm.Invoke($null, @($form, [single](96 * $scale)))
                    $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000)
                    $form.ShowInTaskbar = $false; $form.Show(); [System.Windows.Forms.Application]::DoEvents()
                    $panel = @($form.Controls.Find('panelLinkProperties', $false))
                    $size = $form.Size
                    foreach ($state in 'default', 'large', 'small', 'dpi') {
                        switch ($state) {
                            'large' { $form.Size = New-Object System.Drawing.Size([int]($size.Width * 1.3), [int]($size.Height * 1.2)) }
                            'small' { $form.Size = New-Object System.Drawing.Size([int]($size.Width * 0.8), [int]($size.Height * 0.8)) }
                            'dpi' {
                                # The window moves to a monitor with the next larger scaling: the font, and with
                                # it the layout, grows and the buttons are fitted again.
                                $form.Size = $size
                                $to = $scale + 0.25
                                $fonts2 += New-Object System.Drawing.Font($f[0], [single]($f[1] * $to), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
                                $form.Font = $fonts2[-1]
                                [void]$fitForm.Invoke($null, @($form, [single](96 * $to)))
                            }
                        }
                        foreach ($page in $(if ($panel.Count -eq 1) { 'joint page', 'link page' } else { 'form' })) {
                            if ($panel.Count -eq 1) {
                                $panel[0].Visible = $page -eq 'link page'
                                foreach ($c in $form.Controls) { if ($c -ne $panel[0]) { $c.Visible = $page -eq 'joint page' } }
                            }
                            [System.Windows.Forms.Application]::DoEvents()
                            $problems = New-Object System.Collections.ArrayList
                            Test-ButtonRow $form $page "$($type.Name) [$language $($f[0]) $($f[1])pt x$scale $state]" $problems
                            foreach ($p in $problems) { [void]$report.Add($p) }
                            $total += $problems.Count
                        }
                    }
                } finally { $form.Dispose(); foreach ($old in $fonts2) { $old.Dispose() } }
            }
        }
    }
}
                    $pages = if ($panel.Count -eq 1) { 'joint page', 'link page' } else { 'form' }
                    foreach ($page in $pages) {
                        $root = $form
                        if ($panel.Count -eq 1) {
                            $panel[0].Visible = $page -eq 'link page'
                            foreach ($c in $form.Controls) { if ($c -ne $panel[0]) { $c.Visible = $page -eq 'joint page' } }
                            $root = if ($page -eq 'link page') { $panel[0] } else { @($form.Controls.Find('panelJointPage', $false))[0] }
                        }
                        # A box being edited shows its whole text: keep the focus out of the boxes.
                        $form.ActiveControl = $null
                        [System.Windows.Forms.Application]::DoEvents()
                        $problems = New-Object System.Collections.ArrayList
                        $label = "$($type.Name) [$language $($f[0]) $($f[1])pt x$scale] $page"
                        $boxes = @(Get-NumberBoxes $root)
                        if ($boxes.Count -lt 20) { [void]$problems.Add("${label} : only $($boxes.Count) number boxes found") }
                        foreach ($box in $boxes) {
                            $box.Visible = $true  # the mimic boxes are hidden until needed; a hidden box draws nothing
                            Test-NumberBox $box '2.56527281012E-05' $label $problems
                            if ($box -isnot [System.Windows.Forms.UpDownBase]) {
                                Test-NumberBox $box '-2.56527281097123E-05' $label $problems
                                Test-NumberBox $box '-0.019999999999999997' $label $problems
                            }
                        }
                        foreach ($p in $problems) { [void]$report.Add($p) }
                        $total += $problems.Count
                    }
                } finally { $form.Dispose(); $font.Dispose() }
            }
        }
    }
}
$wideForm.Dispose()

$report | ForEach-Object { Write-Output $_ }
Write-Output "Layout problems: $total"
if ($total -gt 0) { exit 1 }
