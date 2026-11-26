/*
Copyright (c) 2015 Stephen Brawner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SW2URDF.UI
{
    public partial class AssemblyExportForm : Form, IAlignsFieldLabels
    {
        private static readonly log4net.ILog logger = Logger.GetLogger();

        public ExportHelper Exporter;
        private readonly UiText ui;
        // DPI the layout is currently built for; the window may be drawn at another one.
        private float layoutDpi = UiLayout.ScreenDpi();
        public bool AutoUpdatingForm;
        private bool exportCompleted;
        public AttributeDef saveConfigurationAttributeDef;

        private readonly SldWorks swApp;
        private readonly ModelDoc2 ActiveSWModel;
        private LinkNode previouslySelectedNode;
        private readonly Control[] jointBoxes;
        private readonly Control[] linkBoxes;
        private readonly LinkNode BaseNode;

        // Angles are shown in degrees or radians (issue #155); the model always holds radians.
        private AngleDisplay angles;
        private string captionJointType;
        private bool choosingAngleUnit;

        // Shows the angle boxes and captions in the given unit. Only the display changes.
        private void SetAngleUnit(AngleUnit unit)
        {
            if (angles == null)
            {
                angles = new AngleDisplay(unit);
            }
            else
            {
                angles.SetUnit(unit);
            }
            bool degrees = unit == AngleUnit.Degrees;
            choosingAngleUnit = true;
            comboBoxAngleUnitJoint.SelectedIndex = degrees ? 1 : 0;
            comboBoxAngleUnitLink.SelectedIndex = degrees ? 1 : 0;
            choosingAngleUnit = false;
            label6.Text = degrees ? ui.Get("Orientation (deg)") : ui.Get("Orientation (rad)");
            label36.Text = degrees ? ui.Get("Origin (m, deg)") : ui.Get("Origin (m, rad)");
            label23.Text = degrees ? ui.Get("Origin (m, deg)") : ui.Get("Origin (m, rad)");
            SetJointCaptions(captionJointType);
        }

        private void AngleUnitChosen(object sender, EventArgs e)
        {
            if (choosingAngleUnit)
            {
                return;
            }
            AngleUnit unit = ((ComboBox)sender).SelectedIndex == 1 ? AngleUnit.Degrees : AngleUnit.Radians;
            SetAngleUnit(unit);
            new AngleUnitSetting().Save(unit);
        }

        // Unit captions of the limit, dynamics and safety boxes for a joint type (null: no joint).
        private void SetJointCaptions(string jointType)
        {
            captionJointType = jointType;
            bool degrees = angles != null && angles.Unit == AngleUnit.Degrees;
            if (jointType == "revolute" || jointType == "continuous")
            {
                labelLowerLimit.Text = degrees ? ui.Get("lower (deg)") : ui.Get("lower (rad)");
                labelLimitUpper.Text = degrees ? ui.Get("upper (deg)") : ui.Get("upper (rad)");
                labelEffort.Text = ui.Get("effort (N-m)");
                labelVelocity.Text = degrees ? ui.Get("velocity (deg/s)") : ui.Get("velocity (rad/s)");
                labelFriction.Text = ui.Get("friction (N-m)");
                labelDamping.Text = ui.Get("damping (N-m-s/rad)");
                labelSoftLower.Text = degrees ? ui.Get("soft lower limit (deg)") : ui.Get("soft lower limit (rad)");
                labelSoftUpper.Text = degrees ? ui.Get("soft upper limit (deg)") : ui.Get("soft upper limit (rad)");
                labelKPosition.Text = ui.Get("k position");
                labelKVelocity.Text = ui.Get("k velocity");
            }
            else if (jointType == "prismatic")
            {
                labelLowerLimit.Text = ui.Get("lower (m)");
                labelLimitUpper.Text = ui.Get("upper (m)");
                labelEffort.Text = ui.Get("effort (N)");
                labelVelocity.Text = ui.Get("velocity (m/s)");
                labelFriction.Text = ui.Get("friction (N)");
                labelDamping.Text = ui.Get("damping (N-s/m)");
                labelSoftLower.Text = ui.Get("soft lower limit (m)");
                labelSoftUpper.Text = ui.Get("soft upper limit (m)");
                labelKPosition.Text = ui.Get("k position");
                labelKVelocity.Text = ui.Get("k velocity");
            }
            else if (jointType != null)
            {
                labelLowerLimit.Text = ui.Get("lower");
                labelLimitUpper.Text = ui.Get("upper");
                labelEffort.Text = ui.Get("effort");
                labelVelocity.Text = ui.Get("velocity");
                labelFriction.Text = ui.Get("friction");
                labelDamping.Text = ui.Get("damping");
                labelSoftLower.Text = ui.Get("soft lower limit");
                labelSoftUpper.Text = ui.Get("soft upper limit");
                labelKPosition.Text = ui.Get("k position");
                labelKVelocity.Text = ui.Get("k velocity");
            }
            AlignFieldLabels();
        }

        // Room beyond the default window size (issue #156). Joint page: the tree takes half of the
        // extra width and all of the extra height; the property columns move right by the other half
        // and keep their layout, only the boxes holding names widen. Link page: the same split between
        // the tree and the property groups, whose texture path box widens.
        //
        // Below the default width the trees give up width first while the property area keeps its
        // size and moves left, down to a tree of TreeMinimumWidth; narrower than that the page scrolls.
        // The form opens compact, with trees of TreeCompactWidth (designer pixels).
        private const int TreeMinimumWidth = 200;
        private const int TreeCompactWidth = 300;

        private void SetUpGrowLayout()
        {
            GrowLayout grow = GrowLayout.For(this);
            foreach (Control c in panelJointContent.Controls)
            {
                // The limit source text in the header is placed by FitHeader.
                if (c != treeViewJointTree && c != labelLimitSource && c.Left >= treeViewJointTree.Right)
                {
                    grow.Set(c, 0.5f, 0f, false).SetShrink(c, 1f, 0f);
                }
            }
            grow.Set(treeViewJointTree, 0f, 0.5f, true).SetShrink(treeViewJointTree, 0f, 1f);
            grow.Set(textBoxJointName, 0.5f, 0.5f, false);
            grow.Set(label62, 1f, 0f, false);
            grow.Set(comboBoxJointType, 1f, 0f, false);
            grow.Set(labelAngleUnitJoint, 1f, 0f, false);
            grow.Set(comboBoxAngleUnitJoint, 1f, 0f, false);
            grow.Set(labelAngleUnitLink, 1f, 0f, false).SetShrink(labelAngleUnitLink, 1f, 0f);
            grow.Set(comboBoxAngleUnitLink, 1f, 0f, false).SetShrink(comboBoxAngleUnitLink, 1f, 0f);
            grow.Set(comboBoxOrigin, 0.5f, 0.5f, false);
            grow.Set(comboBoxAxis, 0.5f, 0.5f, false);

            grow.Set(treeViewLinkProperties, 0f, 0.5f, true).SetShrink(treeViewLinkProperties, 0f, 1f);
            grow.Set(labelConfigurationStatus, 0f, 1f, false).SetShrink(labelConfigurationStatus, 0f, 1f);
            grow.Set(groupBox5, 0.5f, 0.5f, false).SetShrink(groupBox5, 1f, 0f);
            grow.Set(groupBox4, 0.5f, 0.5f, false).SetShrink(groupBox4, 1f, 0f);
            grow.Set(textBoxTexture, 0f, 0.5f, false);
            grow.Set(buttonTextureBrowse, 0.5f, 0f, false);

            float scale = DesignerScale().Width;
            int narrowestTree = Math.Min(treeViewJointTree.Width, treeViewLinkProperties.Width);
            int widestTree = Math.Max(treeViewJointTree.Width, treeViewLinkProperties.Width);
            grow.MaxShrink = Math.Max(0, narrowestTree - (int)Math.Round(TreeMinimumWidth * scale));
            grow.AfterApply = FitHeader;
            labelLimitSource.SizeChanged += delegate { FitHeader(); };
            labelConfigurationStatus.SizeChanged += delegate { FitHeader(); };
            grow.Capture();
            Width -= Math.Min(grow.MaxShrink, Math.Max(0, widestTree - (int)Math.Round(TreeCompactWidth * scale)));
        }

        // The page headers: the joint page description wraps before the limit source text, and each
        // tree starts below the header text above it (the description, the configuration banner),
        // which gets taller when the window and so the header get narrower.
        private void FitHeader()
        {
            // Not in the middle of a rescale or a refit: controls are then partly scaled or at their
            // default bounds. The layout runs this again when it resumes.
            GrowLayout grow = GrowLayout.Find(this);
            if (grow != null && grow.IsSuspended)
            {
                return;
            }
            int gap = Math.Max(6, Font.Height / 2);
            SizeF scale = DesignerScale();
            // The angle unit is a setting of the whole form: the same place at the top right of the
            // header of both pages, at the right end of the property area. The banner and the limit
            // source text end before it.
            comboBoxAngleUnitLink.Left = groupBox5.Right - comboBoxAngleUnitLink.Width;
            comboBoxAngleUnitJoint.Left = comboBoxAngleUnitLink.Left;
            UiText.AlignLabel(labelAngleUnitJoint, comboBoxAngleUnitJoint);
            UiText.AlignLabel(labelAngleUnitLink, comboBoxAngleUnitLink);
            labelConfigurationStatus.Width = Math.Max(1, labelAngleUnitLink.Left - gap - labelConfigurationStatus.Left);
            // The limit source text starts where the property area starts, but never over the title.
            labelLimitSource.Left = Math.Max(label64.Left - (int)Math.Round(18 * scale.Width), label7.Right + gap);
            labelLimitSource.MaximumSize = new Size(Math.Max(1, labelAngleUnitJoint.Left - gap - labelLimitSource.Left), 0);
            // Room for the limit text above the parent link row, for the width it has now.
            MakeJointHeaderRoom();
            int descriptionWidth = Math.Max(1, labelLimitSource.Left - gap - label69.Left);
            label69.MaximumSize = new Size(descriptionWidth, 0);
            // Measured, not read back: the label may not have resized to the new width yet.
            int descriptionHeight = label69.GetPreferredSize(new Size(descriptionWidth, 0)).Height;
            // Designer tops are in content coordinates; a scrolled panel shows them shifted.
            int jointTop = Math.Max((int)Math.Round(82 * scale.Height) + panelJointContent.AutoScrollPosition.Y,
                label69.Top + descriptionHeight + gap);
            int linkTop = Math.Max((int)Math.Round(74 * scale.Height) + panelLinkContent.AutoScrollPosition.Y,
                label2.Bottom + gap);
            if (labelConfigurationStatus.Left < treeViewLinkProperties.Right)
            {
                linkTop = Math.Max(linkTop, labelConfigurationStatus.Bottom + gap);
            }
            SetTop(treeViewJointTree, jointTop);
            SetTop(treeViewLinkProperties, linkTop);
        }

        // Size of the form font relative to the font the designer layout was made with (its
        // AutoScaleDimensions; WinForms overwrites that property once it has scaled the form).
        private SizeF DesignerScale()
        {
            SizeF now = CurrentAutoScaleDimensions;
            return new SizeF(now.Width / 6F, now.Height / 12F);
        }

        // Moves the top edge of a control and keeps its bottom edge.
        private static void SetTop(Control control, int top)
        {
            if (control.Top != top)
            {
                int bottom = control.Bottom;
                control.SetBounds(control.Left, top, control.Width, Math.Max(1, bottom - top));
            }
        }

        void IAlignsFieldLabels.AlignFieldLabels()
        {
            AlignFieldLabels();
        }

        // The joint page header holds the limit source text, four lines when the limits come from a
        // limit mate (one of them may wrap), to the right of the page description. Leave room for
        // five lines, or for the text on display when it is longer, above the parent link row (moving
        // the property area down when the font's lines are taller than the designer's), and let the
        // description wrap before the limit text. Running it again changes nothing.
        private void MakeJointHeaderRoom()
        {
            // Not in the middle of a rescale (see FitHeader).
            GrowLayout layout = GrowLayout.Find(this);
            if (layout != null && layout.IsSuspended)
            {
                return;
            }
            int gap = Math.Max(4, Font.Height / 3);
            label69.MaximumSize = new Size(Math.Max(1, labelLimitSource.Left - gap - label69.Left), 0);
            // Measured the way the label sizes itself, with a Chinese character too: Windows may draw
            // it with a taller linked font.
            int room;
            using (Label sample = new Label { AutoSize = true, Font = labelLimitSource.Font, Padding = labelLimitSource.Padding,
                Text = "極Ag\n極Ag\n極Ag\n極Ag\n極Ag" })
            {
                room = sample.PreferredHeight;
            }
            // The text on display counts when it is longer, but not while the label is squeezed to a
            // width no real text fits in (during a layout change), where it would measure very tall.
            if (labelLimitSource.MaximumSize.Width == 0 || labelLimitSource.MaximumSize.Width >= 12 * Font.Height)
            {
                room = Math.Max(room, labelLimitSource.PreferredHeight);
            }
            // The property area is moved by an amount that is set, not added to, so it can also move
            // back; a font change (DPI rescale) has already scaled the earlier move.
            float scale = CurrentAutoScaleDimensions.Height;
            if (headerShiftScale > 0 && Math.Abs(headerShiftScale - scale) > 0.01f)
            {
                headerShift = (int)Math.Round(headerShift * scale / headerShiftScale);
            }
            headerShiftScale = scale;
            int rowTop = label64.Top - headerShift;
            int wanted = Math.Max(0, labelLimitSource.Top + room + gap - rowTop);
            int move = wanted - headerShift;
            if (move == 0)
            {
                return;
            }
            foreach (Control c in panelJointContent.Controls)
            {
                if (c != labelLimitSource && c.Left >= treeViewJointTree.Right && c.Top >= label64.Top - gap)
                {
                    c.Top += move;
                }
            }
            headerShift = wanted;
        }

        private void PlaceBeside(Label value, Label caption)
        {
            value.Left = caption.Left + caption.PreferredWidth + Math.Max(4, Font.Height / 2);
            value.Top = caption.Top + (caption.Height - value.Height) / 2;
        }

        // How far MakeJointHeaderRoom has moved the property area, and the layout scale (the form
        // font's autoscale height) it was set for: a DPI rescale scales the move with that.
        private int headerShift;
        private float headerShiftScale;

        private void AlignFieldLabels()
        {
            MakeJointHeaderRoom();
            // The parent and child link names follow their captions on the same line: the designer
            // left no room for a wider caption font.
            PlaceBeside(labelParent, label64);
            PlaceBeside(labelChild, label65);
            label63.Left = textBoxJointName.Left;
            label62.Left = comboBoxJointType.Left;
            UiText.AlignLabel(label14, textBoxIxx);
            UiText.AlignLabel(label11, textBoxIxz);
            UiText.AlignLabel(label13, textBoxInertialOriginX);
            UiText.AlignLabel(label46, textBoxInertialOriginYaw);
            UiText.AlignLabel(label49, textBoxIyy);
            UiText.AlignLabel(label45, textBoxInertialOriginPitch);
            UiText.AlignLabel(label16, textBoxInertialOriginRoll);
            UiText.AlignLabel(label17, textBoxInertialOriginY);
            UiText.AlignLabel(label48, textBoxIyz);
            UiText.AlignLabel(label47, textBoxInertialOriginZ);
            UiText.AlignLabel(label50, textBoxIxy);
            UiText.AlignLabel(label18, textBoxIzz);
            UiText.AlignLabel(label12, textBoxMass);
            UiText.AlignLabel(label24, textBoxVisualOriginZ);
            UiText.AlignLabel(label20, textBoxVisualOriginYaw);
            UiText.AlignLabel(label21, textBoxVisualOriginPitch);
            UiText.AlignLabel(label22, textBoxVisualOriginRoll);
            UiText.AlignLabel(label33, domainUpDownAlpha);
            UiText.AlignLabel(label30, domainUpDownRed);
            UiText.AlignLabel(label31, domainUpDownGreen);
            UiText.AlignLabel(label26, textBoxVisualOriginX);
            UiText.AlignLabel(label32, domainUpDownBlue);
            UiText.AlignLabel(label25, textBoxVisualOriginY);
            UiText.AlignLabel(labelKVelocity, textBoxKVelocity);
            UiText.AlignLabel(labelSoftLower, textBoxSoftLower);
            UiText.AlignLabel(labelSoftUpper, textBoxSoftUpper);
            UiText.AlignLabel(labelKPosition, textBoxKPosition);
            UiText.AlignLabel(labelFriction, textBoxFriction);
            UiText.AlignLabel(labelDamping, textBoxDamping);
            UiText.AlignLabel(label7CalibrationRising, textBoxCalibrationRising);
            UiText.AlignLabel(label73, textBoxCalibrationFalling);
            UiText.AlignLabel(labelVelocity, textBoxLimitVelocity);
            UiText.AlignLabel(labelLowerLimit, textBoxLimitLower);
            UiText.AlignLabel(labelLimitUpper, textBoxLimitUpper);
            UiText.AlignLabel(labelEffort, textBoxLimitEffort);
            UiText.AlignLabel(label58, textBoxAxisX);
            UiText.AlignLabel(label59, textBoxAxisY);
            UiText.AlignLabel(label61, textBoxAxisZ);
            UiText.AlignLabel(label52, textBoxJointX);
            UiText.AlignLabel(label56, textBoxJointYaw);
            UiText.AlignLabel(label55, textBoxJointPitch);
            UiText.AlignLabel(label51, textBoxJointRoll);
            UiText.AlignLabel(label53, textBoxJointY);
            UiText.AlignLabel(label57, textBoxJointZ);
            UiText.AlignLabel(label67, comboBoxAxis);
            UiText.AlignLabel(label66, comboBoxOrigin);
            UiText.AlignLabel(MimicMultiplierLabel, textBoxMimicMultiplier);
            UiText.AlignLabel(MimicOffsetLabel, textBoxMimicOffset);
            UiText.PlaceAfter(label1, label54);
            UiText.PlaceAfter(AxisRequiredLabel, label60);
            UiText.PlaceAfter(LimitRequiredLabel, label68);
            // The group titles of the Origin/Axis/Limit row sit right above the "Position (m)"
            // caption; with the font's real line height they touch it. Lift the whole title row.
            Label[] titles = { label54, label1, label60, AxisRequiredLabel, label68, LimitRequiredLabel };
            int bottom = Math.Max(label54.Bottom, label1.Bottom);
            int lift = bottom + Math.Max(2, Font.Height / 6) - label3.Top;
            if (lift > 0)
            {
                foreach (Label title in titles)
                {
                    title.Top -= lift;
                }
            }
            FitHeader();
        }

        public AssemblyExportForm(SldWorks SwApp, LinkNode node, ExportHelper exporter)
        {
            Application.ThreadException +=
                new ThreadExceptionEventHandler(ExceptionHandler);
            AppDomain.CurrentDomain.UnhandledException +=
                new UnhandledExceptionEventHandler(UnhandledException);
            ui = UiText.FromSolidWorks(SwApp);
            // One font for the whole form, chosen for the UI language (issue #157).
            Font = ui.CreateFormFont();
            InitializeComponent();
            ui.Apply(this);
            // The mesh format captions are bare names; what each format keeps is in the tooltip.
            var meshFormatTips = new ToolTip();
            meshFormatTips.SetToolTip(radioButtonStl, ui.Get("Geometry only, no colour."));
            meshFormatTips.SetToolTip(radioButton3dxml, ui.Get("Geometry with colour."));
            meshFormatTips.SetToolTip(radioButtonObj, ui.Get("Geometry with colour and materials, as .obj and .mtl files."));
            meshFormatTips.SetToolTip(checkBoxXacro, ui.Get("Write the robot as a .xacro file instead of a .urdf file. The launch files then expand it with xacro."));
            // Items show the localized caption; the stored/compared value is mapped back by ui.ComboValue.
            comboBoxJointType.Items.Clear();
            foreach (string type in SW2URDF.URDF.Joint.AvailableTypes)
                comboBoxJointType.Items.Add(ui.ComboDisplay(type));
            // The designer width fits the English identifiers; the longer captions must not be clipped.
            int widest = 0;
            foreach (object item in comboBoxJointType.Items)
                widest = Math.Max(widest, TextRenderer.MeasureText(item.ToString(), comboBoxJointType.Font).Width);
            int needed = widest + SystemInformation.VerticalScrollBarWidth + 10;
            comboBoxJointType.DropDownWidth = Math.Max(comboBoxJointType.Width, needed);
            if (needed > comboBoxJointType.Width)
            {
                int room = comboBoxJointType.Parent.ClientSize.Width - comboBoxJointType.Left - 8;
                comboBoxJointType.Width = Math.Min(needed, room);
            }
            // Angle unit: remembered per user, radians unless the user chose degrees.
            SetAngleUnit(new AngleUnitSetting().Load());
            string angleTip = ui.Get("Angles are shown in this unit. The URDF and the saved settings always use radians.");
            meshFormatTips.SetToolTip(comboBoxAngleUnitJoint, angleTip);
            meshFormatTips.SetToolTip(comboBoxAngleUnitLink, angleTip);
            comboBoxAngleUnitJoint.SelectedIndexChanged += AngleUnitChosen;
            comboBoxAngleUnitLink.SelectedIndexChanged += AngleUnitChosen;
            AlignFieldLabels();
            // A limit text longer than the room left for it moves the property area down further.
            labelLimitSource.SizeChanged += delegate { MakeJointHeaderRoom(); };
            UiLayout.FitForm(this, UiLayout.ScreenDpi());
            swApp = SwApp;
            BaseNode = node;
            checkBoxXacro.Checked = BaseNode.Link.WriteXacro;
            ActiveSWModel = swApp.ActiveDoc;
            RefreshConfigurationStatus();
            SetUpGrowLayout();
            Exporter = exporter;
            AutoUpdatingForm = false;

            jointBoxes = new Control[] {
                textBoxJointName, comboBoxAxis, comboBoxJointType,
                textBoxAxisX, textBoxAxisY, textBoxAxisZ,
                textBoxJointX, textBoxJointY, textBoxJointZ,
                textBoxJointPitch, textBoxJointRoll, textBoxJointYaw,
                textBoxLimitLower, textBoxLimitUpper, textBoxLimitEffort, textBoxLimitVelocity,
                textBoxDamping, textBoxFriction,
                textBoxCalibrationFalling, textBoxCalibrationRising,
                textBoxSoftLower, textBoxSoftUpper, textBoxKPosition, textBoxKVelocity
            };
            linkBoxes = new Control[] {
                textBoxInertialOriginX, textBoxInertialOriginY, textBoxInertialOriginZ,
                textBoxInertialOriginRoll, textBoxInertialOriginPitch, textBoxInertialOriginYaw,
                textBoxVisualOriginX, textBoxVisualOriginY, textBoxVisualOriginZ,
                textBoxVisualOriginRoll, textBoxVisualOriginPitch, textBoxVisualOriginYaw,
                textBoxIxx, textBoxIxy, textBoxIxz, textBoxIyy, textBoxIyz, textBoxIzz,
                textBoxMass,
                domainUpDownRed, domainUpDownGreen, domainUpDownBlue, domainUpDownAlpha,
                comboBoxMaterials,
                textBoxTexture
            };

            List<TextBox> numericTextBoxes = new List<TextBox>() {
                textBoxAxisX, textBoxAxisY, textBoxAxisZ,
                textBoxJointX, textBoxJointY, textBoxJointZ,
                textBoxJointPitch, textBoxJointRoll, textBoxJointYaw,
                textBoxLimitLower, textBoxLimitUpper, textBoxLimitEffort, textBoxLimitVelocity,
                textBoxDamping, textBoxFriction,
                textBoxCalibrationFalling, textBoxCalibrationRising,
                textBoxSoftLower, textBoxSoftUpper, textBoxKPosition, textBoxKVelocity,
                textBoxInertialOriginX, textBoxInertialOriginY, textBoxInertialOriginZ,
                textBoxInertialOriginRoll, textBoxInertialOriginPitch, textBoxInertialOriginYaw,
                textBoxVisualOriginX, textBoxVisualOriginY, textBoxVisualOriginZ,
                textBoxVisualOriginRoll, textBoxVisualOriginPitch, textBoxVisualOriginYaw,
                textBoxIxx, textBoxIxy, textBoxIxz, textBoxIyy, textBoxIyz, textBoxIzz,
                textBoxMass,
                textBoxMimicMultiplier, textBoxMimicOffset,
            };

            foreach (TextBox textBox in numericTextBoxes)
            {
                textBox.KeyPress += NumericalTextBoxKeyPress;
            }

            saveConfigurationAttributeDef = SwApp.DefineAttribute(ConfigurationSerialization.UrdfConfigurationSwAttributeName);
            int Options = 0;

            saveConfigurationAttributeDef.AddParameter(
                "data", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "name", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "date", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "exporterVersion", (int)swParamType_e.swParamTypeDouble, 1.0, Options);
            saveConfigurationAttributeDef.Register();
        }

        private void ExceptionHandler(object sender, ThreadExceptionEventArgs e)
        {
            logger.Error("Exception encountered in Assembly export form", e.Exception);
            MessageBox.Show("There was a problem with the export form: \n\"" +
                e.Exception.Message + "\"\nEmail your maintainer with the log file found at " +
                Logger.GetFileName());
        }

        private void UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            logger.Error("Unhandled exception in Assembly Export form", ex);
            MessageBox.Show("There was a problem with the export form: \n\"" +
                ex.Message + "\"\nEmail your maintainer with the log file found at " +
                Logger.GetFileName());
        }

        //Joint form configuration controls
        // The window DPI is only known once the handle exists, and changes when the form is moved to a
        // monitor with another scale: fit the buttons again for the DPI the form is really drawn at.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UiLayout.ShowFullValueTips(this);
            UiLayout.FollowWindowDpi(this, ref layoutDpi);
        }

        // Closing the preview in any way other than a completed export (Cancel, the window's close
        // button, Escape) takes back what the preview put into the model.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (!exportCompleted && Exporter != null)
            {
                Exporter.DiscardGeneratedFeatures();
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == UiLayout.WM_DPICHANGED)
            {
                UiLayout.FollowWindowDpi(this, ref layoutDpi);
            }
        }

        private void AssemblyExportFormLoad(object sender, EventArgs e)
        {
            Exporter.UpdateReferenceGeometries();
            FillJointTree();
        }

        // The choice is kept on the base link, which is saved with the settings of the active configuration.
        private void CheckBoxXacroCheckedChanged(object sender, EventArgs e)
        {
            if (BaseNode != null)
            {
                BaseNode.Link.WriteXacro = checkBoxXacro.Checked;
            }
        }

        private void RadioButtonObjCheckedChanged(object sender, EventArgs e)
        {
            UpdateObjColorControls();
        }

        private void UpdateObjColorControls()
        {
            bool stl = !radioButtonObj.Checked;

            // OBJ writes no URDF <material>; color and texture come from the MTL file.
            Control[] colorControls = {
                label28, label29, label30, label31, label32, label33, label34,
                comboBoxMaterials, domainUpDownRed, domainUpDownGreen, domainUpDownBlue,
                domainUpDownAlpha, textBoxTexture, buttonTextureBrowse };
            foreach (Control control in colorControls)
            {
                control.Enabled = stl;
            }
            panelObjColorNote.Visible = !stl;
        }

        private void ButtonJointNextClick(object sender, EventArgs e)
        {
            if (!(previouslySelectedNode == null || previouslySelectedNode.Link.Joint == null))
            {
                SaveJointDataFromPropertyBoxes(previouslySelectedNode.Link.Joint);
            }

            string errors = CheckJointsForErrors();
            if (!string.IsNullOrWhiteSpace(errors))
            {
                string message = "The following joints are missing required fields, please " +
                    "address them before continuing\r\n\r\n" + errors;
                ShowJointErrors(message);
                return;
            }
            // Only when the joint page is left: the joint it shows can still be corrected after the message.
            previouslySelectedNode = null;

            while (treeViewJointTree.Nodes.Count > 0)
            {
                LinkNode node = (LinkNode)treeViewJointTree.Nodes[0];
                treeViewJointTree.Nodes.Remove(node);
                BaseNode.Nodes.Add(node);
            }
            ChangeAllNodeFont(BaseNode, new Font(treeViewJointTree.Font, FontStyle.Regular));

            FillLinkTree();
            panelLinkProperties.Visible = true;
            Focus();
        }

        // Overridden by tests, which must not show a window.
        internal virtual void ShowJointErrors(string message)
        {
            MessageBox.Show(message, ui.Get("URDF Joint Errors"));
        }

        private string CheckJointsForErrors()
        {
            StringBuilder builder = new StringBuilder();
            foreach (LinkNode child in treeViewJointTree.Nodes)
            {
                CheckJointsForErrors(child, builder);
            }
            CheckMimicLoops(builder);
            return builder.ToString();
        }

        private void CheckMimicLoops(StringBuilder builder)
        {
            List<Joint> joints = new List<Joint>();
            AddJoints(treeViewJointTree.Nodes, joints);
            foreach (Joint joint in joints)
            {
                if (joint.MimicsItself(joints))
                {
                    builder.Append(string.Format(ui.Get("{0}: its mimic leads back to itself"), joint.Name)).Append("\r\n");
                }
            }
        }

        private static void AddJoints(TreeNodeCollection nodes, List<Joint> joints)
        {
            foreach (LinkNode node in nodes)
            {
                joints.Add(node.Link.Joint);
                AddJoints(node.Nodes, joints);
            }
        }

        private StringBuilder CheckJointsForErrors(LinkNode node, StringBuilder builder)
        {
            if (!node.Link.Joint.AreRequiredFieldsSatisfied() || node.Link.Joint.IsAxisMissing())
            {
                builder.Append(node.Link.Joint.Name).Append("\r\n");
            }

            // A joint that is not in the robot cannot be mimicked. Names are case sensitive in URDF.
            string mimicked = node.Link.Joint.Mimic?.JointName;
            if (mimicked != null && !Exporter.GetJointNames().Contains(mimicked))
            {
                builder.Append(string.Format(ui.Get("{0}: the joint it mimics, \"{1}\", is not in the robot"),
                    node.Link.Joint.Name, mimicked)).Append("\r\n");
            }

            foreach (LinkNode child in node.Nodes)
            {
                CheckJointsForErrors(child, builder);
            }
            return builder;
        }

        private void Button_Joint_Cancel_Click(object sender, EventArgs e)
        {
            if (previouslySelectedNode != null)
            {
                SaveJointDataFromPropertyBoxes(previouslySelectedNode.Link.Joint);
            }
            while (treeViewJointTree.Nodes.Count > 0)
            {
                LinkNode node = (LinkNode)treeViewJointTree.Nodes[0];
                treeViewJointTree.Nodes.Remove(node);
                BaseNode.Nodes.Add(node);
            }

            // The joint names go back to what they were before the preview, then the tree is saved
            Exporter.DiscardGeneratedFeatures();
            SaveConfigTree(ActiveSWModel, BaseNode, true);
            Close();
        }

        private void ButtonLinksCancelClick(object sender, EventArgs e)
        {
            if (previouslySelectedNode != null)
            {
                SaveLinkDataFromPropertyBoxes(previouslySelectedNode.Link);
            }

            // The joint names go back to what they were before the preview, then the tree is saved
            Exporter.DiscardGeneratedFeatures();
            SaveConfigTree(ActiveSWModel, BaseNode, true);
            Close();
        }

        private void ButtonLinksPreviousClick(object sender, EventArgs e)
        {
            LinkNode node = (LinkNode)treeViewLinkProperties.SelectedNode;
            if (node != null)
            {
                SaveLinkDataFromPropertyBoxes(node.Link);
            }
            previouslySelectedNode = null;
            ChangeAllNodeFont(BaseNode, new Font(treeViewJointTree.Font, FontStyle.Regular));
            FillJointTree();
            panelLinkProperties.Visible = false;
        }

        private void ButtonLinksFinishClick(object sender, EventArgs e)
        {
            FinishExport(true);
        }

        private void ButtonLinksExportUrdfOnlyClick(object sender, EventArgs e)
        {
            FinishExport(false);
        }

        private void FinishExport(bool exportSTL)
        {
            logger.Info("Completing URDF export");
            SaveConfigTree(ActiveSWModel, BaseNode, false);

            // Saving selected node
            LinkNode node = (LinkNode)treeViewLinkProperties.SelectedNode;
            if (node != null)
            {
                SaveLinkDataFromPropertyBoxes(node.Link);
            }

            Exporter.URDFRobot = CreateRobotFromTreeView(treeViewLinkProperties);

            // The UI should prevent these sorts of errors, but just in case
            string errors = CheckLinksForErrors(Exporter.URDFRobot.BaseLink);
            if (!string.IsNullOrWhiteSpace(errors))
            {
                logger.Info("Link errors encountered:\n " + errors);

                string message = "The following links contained errors in either their link or joint " +
                    "properties. Please address before continuing\r\n\r\n" + errors;
                MessageBox.Show(message, ui.Get("URDF Errors"));
                return;
            }

            string warnings = CheckLinksForWarnings(Exporter.URDFRobot.BaseLink);

            if (!string.IsNullOrWhiteSpace(warnings))
            {
                logger.Info("Link warnings encountered:\r\n" + warnings);

                string message = "The following links contained issues that may cause problems. " +
                "Do you wish to proceed?\r\n\r\n" + warnings;
                DialogResult result =
                    MessageBox.Show(message, ui.Get("URDF Warnings"), MessageBoxButtons.YesNo);

                if (result == DialogResult.No)
                {
                    logger.Info("Export canceled for user to review warnings");
                    return;
                }
            }

            SaveFileDialog saveFileDialog1 = new SaveFileDialog
            {
                RestoreDirectory = true,
                InitialDirectory = Exporter.SavePath,
                FileName = Exporter.PackageName
            };

            bool saveResult = DialogResult.OK == saveFileDialog1.ShowDialog();
            saveFileDialog1.Dispose();
            if (saveResult)
            {
                Exporter.SavePath = Path.GetDirectoryName(saveFileDialog1.FileName);
                Exporter.PackageName = Path.GetFileName(saveFileDialog1.FileName);

                logger.Info("Saving URDF package to " + saveFileDialog1.FileName);

                MeshExportFormat meshFormat;
                if(radioButtonStl.Checked)
                {
                    meshFormat = MeshExportFormat.STL;
                }
                else if(radioButtonObj.Checked)
                {
                    meshFormat = MeshExportFormat.OBJ;
                }
                else if(radioButton3dxml.Checked)
                {
                    meshFormat = MeshExportFormat.THREEDXML;
                }
                else
                {
                    meshFormat = MeshExportFormat.STL;
                }
                Exporter.URDFRobot.BaseLink.WriteXacro = checkBoxXacro.Checked;
                Exporter.ExportRobot(exportSTL, meshFormat);
                ConfigurationSerialization.GroupUrdfFeatures(ActiveSWModel, ui);

                // A completed export keeps the generated features
                exportCompleted = true;
                Close();
            }
        }

        private string CheckLinksForErrors(Link baseLink)
        {
            StringBuilder builder = new StringBuilder();
            CheckLinkForErrors(baseLink, builder);
            return builder.ToString();
        }

        private StringBuilder CheckLinkForErrors(Link link, StringBuilder builder)
        {
            if (!link.AreRequiredFieldsSatisfied())
            {
                builder.Append(link.Name).Append("\r\n");
            }
            foreach (Link child in link.Children)
            {
                CheckLinkForErrors(child, builder);
            }
            return builder;
        }

        private void TreeViewLinkPropertiesAfterSelect(object sender, TreeViewEventArgs e)
        {
            Font fontRegular = new Font(treeViewJointTree.Font, FontStyle.Regular);
            Font fontBold = new Font(treeViewJointTree.Font, FontStyle.Bold);
            if (e.Node == null)
            {
                return;
            }
            if (previouslySelectedNode != null)
            {
                SaveLinkDataFromPropertyBoxes(previouslySelectedNode.Link);
                previouslySelectedNode.NodeFont = fontRegular;
            }
            LinkNode node = (LinkNode)e.Node;
            node.NodeFont = fontBold;
            node.Text = node.Text;
            ActiveSWModel.ClearSelection2(true);
            SelectionMgr manager = ActiveSWModel.SelectionManager;

            SelectData data = manager.CreateSelectData();
            data.Mark = -1;
            foreach (Component2 component in node.Link.SWComponents)
            {
                component.Select4(true, data, false);
            }
            FillLinkPropertyBoxes(node.Link);
            treeViewLinkProperties.Focus();
            previouslySelectedNode = node;
        }

        /// <summary>
        /// Validates text entry for numerical text boxes to limit improper input. It's not perfect
        /// because you can still copy and paste bad input into the fields, but that's addressed
        /// elsewhere
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NumericalTextBoxKeyPress(object sender, KeyPressEventArgs e)
        {
            // In most cases, if we can't parse what they are trying to type, then it's not
            // valid input.
            TextBox textBox = (TextBox)sender;
            string potentialText = textBox.Text + e.KeyChar;
            bool parseSuccess =
                double.TryParse(potentialText,
                    URDFAttribute.URDFNumberStyle,
                    URDFAttribute.URDFNumberFormat,
                    out _);

            // If the key pressed is not a digit, +/- sign or the decimal separator than ignore it (e.Handled = true)
            e.Handled = (!parseSuccess &&
                         !char.IsControl(e.KeyChar) &&
                         !char.IsDigit(e.KeyChar) &&
                         potentialText != "-" &&
                         potentialText != "+");
        }

        #region Link Properties Controls Handlers

        private void ButtonTextureBrowseClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog1 = new OpenFileDialog
            {
                RestoreDirectory = true
            };

            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                textBoxTexture.Text = openFileDialog1.FileName;
            }
        }

        #endregion Link Properties Controls Handlers

        #region Joint Properties Controls Handlers

        private void TreeViewJointtreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            Font fontRegular = new Font(treeViewJointTree.Font, FontStyle.Regular);
            Font fontBold = new Font(treeViewJointTree.Font, FontStyle.Bold);
            if (e.Node == null)
            {
                return;
            }
            if (previouslySelectedNode != null && !previouslySelectedNode.IsBaseNode)
            {
                SaveJointDataFromPropertyBoxes(previouslySelectedNode.Link.Joint);
            }
            if (previouslySelectedNode != null)
            {
                previouslySelectedNode.NodeFont = fontRegular;
            }
            LinkNode node = (LinkNode)e.Node;
            ActiveSWModel.ClearSelection2(true);
            SelectionMgr manager = ActiveSWModel.SelectionManager;

            SelectData data = manager.CreateSelectData();
            data.Mark = -1;
            foreach (Component2 component in node.Link.SWComponents)
            {
                component.Select4(true, data, false);
            }
            node.NodeFont = fontBold;
            node.Text = node.Text;
            FillJointPropertyBoxes(node.Link.Joint);
            previouslySelectedNode = node;
        }

        private void ComboBoxAxisSelectedIndexChanged(object sender, EventArgs e)
        {
            if (!AutoUpdatingForm)
            {
                if (!(String.IsNullOrWhiteSpace(comboBoxOrigin.Text) ||
                    String.IsNullOrWhiteSpace(comboBoxAxis.Text)))
                {
                    double[] Axis = Exporter.EstimateAxis(comboBoxAxis.Text);
                    Axis = Exporter.LocalizeAxis(Axis, comboBoxOrigin.Text);
                    textBoxAxisX.Text = Axis[0].ToString("R", SW2URDF.URDF.URDFAttribute.URDFNumberFormat);
                    textBoxAxisY.Text = Axis[1].ToString("R", SW2URDF.URDF.URDFAttribute.URDFNumberFormat);
                    textBoxAxisZ.Text = Axis[2].ToString("R", SW2URDF.URDF.URDFAttribute.URDFNumberFormat);
                }
            }
        }

        #endregion Joint Properties Controls Handlers

        private void ShowMimicControls(bool showControls)
        {
            MimicEquationLabel.Visible = showControls;
            MimicJointComboBox.Visible = showControls;
            MimicJointLabel.Visible = showControls;
            MimicMultiplierLabel.Visible = showControls;
            textBoxMimicMultiplier.Visible = showControls;
            MimicOffsetLabel.Visible = showControls;
            textBoxMimicOffset.Visible = showControls;
        }

        private void MimicCheckBoxCheckedChanged(object sender, EventArgs e)
        {
            bool showControls = (sender as CheckBox).Checked;
            ShowMimicControls(showControls);
            textBoxMimicMultiplier.Text = "1.0";
            textBoxMimicOffset.Text = "0.0";
        }
    }
}