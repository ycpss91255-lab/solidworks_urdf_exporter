using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Showing the joint page of a fixed joint does not change its axis, and a
    /// joint that needs an axis is reported when it has none (issue #195). The page is filled, saved and
    /// checked by the code of the form.
    /// </summary>
    public class TestFixedJointAxis
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] BoxNames = { "textBoxAxisX", "textBoxAxisY", "textBoxAxisZ" };

        private static readonly string[] MovingTypes = { "revolute", "continuous", "prismatic", "planar" };

        // Values that a text with 12 digits does not give back.
        private static readonly double[] Tilted = { 1.0 / 3, -0.70710678118654757, 0.62360956446232355 };

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Members).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        private static Joint NewJoint(string name, string type, params double[] axis)
        {
            Link parent = new Link { Name = "base_link" };
            Link child = new Link(parent) { Name = name + "_link" };
            child.Joint.Name = name;
            child.Joint.Type = type;
            child.Joint.Parent.Name = parent.Name;
            child.Joint.Child.Name = child.Name;
            child.Joint.Axis.SetXYZ(axis);
            child.Joint.CoordinateSystemName = "Origin_global";
            child.Joint.AxisName = "Axis_1";
            child.Joint.Limit.Lower = -1;
            child.Joint.Limit.Upper = 1;
            child.Joint.Limit.Effort = 10;
            child.Joint.Limit.Velocity = 2;
            return child.Joint;
        }

        // The assembly form and its exporter without their constructors, which need SolidWorks. The form is
        // never shown.
        private static AssemblyExportForm NewForm()
        {
            ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            SetField(exporter, "ReferenceCoordinateSystemNames", new List<string> { "Origin_global" });
            SetField(exporter, "ReferenceAxesNames", new List<string> { "Axis_1" });
            Robot robot = new Robot();
            robot.SetBaseLink(new Link { Name = "base_link" });
            exporter.URDFRobot = robot;

            AssemblyExportForm form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            SetField(form, "ui", new UiText("english"));
            typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            SetField(form, "jointBoxes", new[]
            {
                "textBoxJointName", "comboBoxAxis", "comboBoxJointType", "textBoxLimitLower", "textBoxLimitUpper",
                "textBoxLimitEffort", "textBoxLimitVelocity"
            }.Concat(BoxNames).Select(name => Field<Control>(form, name)).ToArray());
            ComboBox types = Field<ComboBox>(form, "comboBoxJointType");
            types.Items.Clear();
            types.Items.AddRange(Joint.AvailableTypes.Cast<object>().ToArray());
            form.Exporter = exporter;
            MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
            if (setUnit != null)
            {
                setUnit.Invoke(form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
            }
            return form;
        }

        private static string[] Boxes(AssemblyExportForm form)
        {
            return BoxNames.Select(name => Field<TextBox>(form, name).Text).ToArray();
        }

        // The user shows the page of the joint, chooses a type and leaves the page.
        private static void ChooseType(AssemblyExportForm form, Joint joint, string type)
        {
            form.FillJointPropertyBoxes(joint);
            Field<ComboBox>(form, "comboBoxJointType").Text = type;
            form.SaveJointDataFromPropertyBoxes(joint);
            Assert.Equal(type, joint.Type);
            Joint other = NewJoint("other_joint", "revolute", 0.5, 0.5, 0);
            form.FillJointPropertyBoxes(other);
            form.SaveJointDataFromPropertyBoxes(other);
        }

        // The names the form lists in the message about missing required fields.
        private static string MissingFields(AssemblyExportForm form, Joint joint)
        {
            Link link = new Link { Name = "arm" };
            link.Joint.SetElement(joint);
            TreeView tree = Field<TreeView>(form, "treeViewJointTree");
            tree.Nodes.Clear();
            tree.Nodes.Add(new LinkNode { Link = link });
            return (string)typeof(AssemblyExportForm).GetMethod("CheckJointsForErrors", Members, null, Type.EmptyTypes, null)
                .Invoke(form, null);
        }

        [Fact]
        public void ShowingAFixedJointKeepsItsAxis()
        {
            foreach (double[] axis in new[] { new double[] { 0, 0, 1 }, Tilted })
            {
                Joint joint = NewJoint("arm_joint", "fixed", axis);
                using (AssemblyExportForm form = NewForm())
                {
                    ChooseType(form, joint, "fixed");
                    Assert.Equal(axis, joint.Axis.GetXYZ());
                    ChooseType(form, joint, "fixed");
                    Assert.Equal(axis, joint.Axis.GetXYZ());
                }
            }
        }

        [Fact]
        public void ChangingToFixedAndBackKeepsTheAxis()
        {
            foreach (string type in MovingTypes)
            {
                Joint joint = NewJoint("arm_joint", type, Tilted);
                using (AssemblyExportForm form = NewForm())
                {
                    ChooseType(form, joint, "fixed");
                    ChooseType(form, joint, "fixed");
                    ChooseType(form, joint, type);
                    Assert.Equal(Tilted, joint.Axis.GetXYZ());
                    Assert.Equal("Axis_1", joint.AxisName);
                    Assert.Equal("", MissingFields(form, joint));
                }
            }
        }

        [Fact]
        public void PageOfAFixedJointShowsTheAxisItKeeps()
        {
            using (AssemblyExportForm form = NewForm())
            {
                form.FillJointPropertyBoxes(NewJoint("arm_joint", "fixed", 0, 0.5, -1));
                Assert.Equal(new[] { "0", "0.5", "-1" }, Boxes(form));
                // Not the one of the joint shown before.
                form.FillJointPropertyBoxes(NewJoint("arm_joint", "fixed", 0, 0, 0));
                Assert.Equal(new[] { "0", "0", "0" }, Boxes(form));
            }
        }

        [Fact]
        public void AxisTypedForAFixedJointIsSaved()
        {
            Joint joint = NewJoint("arm_joint", "fixed", Tilted);
            using (AssemblyExportForm form = NewForm())
            {
                form.FillJointPropertyBoxes(joint);
                Field<TextBox>(form, "textBoxAxisX").Text = "0.25";
                form.SaveJointDataFromPropertyBoxes(joint);
            }
            Assert.Equal(new[] { 0.25, Tilted[1], Tilted[2] }, joint.Axis.GetXYZ());
        }

        [Fact]
        public void JointThatNeedsAnAxisAndHasNoneIsReported()
        {
            using (AssemblyExportForm form = NewForm())
            {
                foreach (string type in MovingTypes)
                {
                    Assert.Contains("arm_joint", MissingFields(form, NewJoint("arm_joint", type, 0, 0, 0)));
                    Assert.Equal("", MissingFields(form, NewJoint("arm_joint", type, 0, 1e-9, 0)));
                    // Cleared on the page.
                    Joint joint = NewJoint("arm_joint", type, 0, 0, 1);
                    form.FillJointPropertyBoxes(joint);
                    Field<TextBox>(form, "textBoxAxisZ").Text = "";
                    form.SaveJointDataFromPropertyBoxes(joint);
                    Assert.Contains("arm_joint", MissingFields(form, joint));
                }
                foreach (string type in new[] { "fixed", "floating" })
                {
                    Assert.Equal("", MissingFields(form, NewJoint("arm_joint", type, 0, 0, 0)));
                }
            }
        }
    }
}
