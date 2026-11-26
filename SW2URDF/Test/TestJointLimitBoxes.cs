using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using System.Xml;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The joint page is filled and saved by the form's own code for revolute
    /// and prismatic joints whose limits are not set, set on one side or set (issue #181).
    /// </summary>
    public class TestJointLimitBoxes
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Frame = "Origin_arm_joint";
        private const string AxisName = "Axis_arm_joint";
        private const string Manual = "Limits: entered manually, not read from a limit mate";

        private static readonly string[] LimitedTypes = { "revolute", "prismatic" };

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Members).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        private static string Text(AssemblyExportForm form, string control)
        {
            return Field<Control>(form, control).Text;
        }

        private static void Enter(AssemblyExportForm form, string control, string text)
        {
            Field<Control>(form, control).Text = text;
        }

        private static Joint NewJoint(string type)
        {
            Link parent = new Link { Name = "base_link" };
            Link child = new Link(parent) { Name = "arm" };
            child.Joint.Name = "arm_joint";
            child.Joint.Type = type;
            child.Joint.Parent.Name = parent.Name;
            child.Joint.Child.Name = child.Name;
            child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
            child.Joint.CoordinateSystemName = Frame;
            child.Joint.AxisName = AxisName;
            return child.Joint;
        }

        // The assembly form and its exporter without their constructors, which need SolidWorks. The form is
        // never shown.
        private static AssemblyExportForm NewForm()
        {
            ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
            SetField(exporter, "ReferenceCoordinateSystemNames", new[] { "Origin_global", Frame }.ToList());
            SetField(exporter, "ReferenceAxesNames", new[] { AxisName }.ToList());
            Robot robot = new Robot();
            robot.SetBaseLink(new Link { Name = "base_link" });
            exporter.URDFRobot = robot;

            AssemblyExportForm form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            SetField(form, "ui", new UiText("english"));
            typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            SetField(form, "jointBoxes", new[]
            {
                "textBoxJointName", "comboBoxAxis", "comboBoxJointType",
                "textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitEffort", "textBoxLimitVelocity"
            }.Select(name => Field<Control>(form, name)).ToArray());
            ComboBox types = Field<ComboBox>(form, "comboBoxJointType");
            types.Items.Clear();
            types.Items.AddRange(Joint.AvailableTypes.Cast<object>().ToArray());
            form.Exporter = exporter;
            // The boxes hold radians here, also in a build where the form can show degrees.
            MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
            if (setUnit != null)
            {
                setUnit.Invoke(form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
            }
            return form;
        }

        // The limit element of the joint as it is exported.
        private static XmlElement ExportedLimit(Joint joint)
        {
            StringWriter text = new StringWriter();
            using (XmlWriter writer = XmlWriter.Create(text))
            {
                joint.WriteURDF(writer);
            }
            XmlDocument document = new XmlDocument();
            document.LoadXml(text.ToString());
            return (XmlElement)document.SelectSingleNode("joint/limit");
        }

        private static void FillWholePage(AssemblyExportForm form, Joint joint)
        {
            form.FillJointPropertyBoxes(joint);
            // The last things the page is filled with; they stay unset when the filling stops half way.
            Assert.Equal(Frame, Field<ComboBox>(form, "comboBoxOrigin").SelectedItem);
            Assert.Equal(AxisName, Field<ComboBox>(form, "comboBoxAxis").SelectedItem);
            Assert.False(form.AutoUpdatingForm);
        }

        [Fact]
        public void PageOpensWithBlankLimitBoxes()
        {
            foreach (string type in LimitedTypes)
            {
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, NewJoint(type));
                    Assert.Equal("", Text(form, "textBoxLimitLower"));
                    Assert.Equal("", Text(form, "textBoxLimitUpper"));
                    Assert.Equal(Manual, Text(form, "labelLimitSource"));
                }
            }
        }

        [Fact]
        public void LimitsTypedIntoBlankBoxesAreExported()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, joint);
                    Enter(form, "textBoxLimitLower", "-0.02");
                    Enter(form, "textBoxLimitUpper", "0.03");
                    Enter(form, "textBoxLimitEffort", "10");
                    Enter(form, "textBoxLimitVelocity", "1");
                    form.SaveJointDataFromPropertyBoxes(joint);
                }
                XmlElement limit = ExportedLimit(joint);
                Assert.Equal("-0.02", limit.GetAttribute("lower"));
                Assert.Equal("0.03", limit.GetAttribute("upper"));
            }
        }

        [Fact]
        public void LimitSetOnOneSideIsShown()
        {
            foreach (string type in LimitedTypes)
            {
                Joint lower = NewJoint(type);
                lower.Limit.Lower = -0.25;
                Joint upper = NewJoint(type);
                upper.Limit.Upper = 0.5;
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, lower);
                    Assert.Equal("-0.25", Text(form, "textBoxLimitLower"));
                    Assert.Equal("", Text(form, "textBoxLimitUpper"));
                    Assert.Equal(Manual, Text(form, "labelLimitSource"));
                    FillWholePage(form, upper);
                    Assert.Equal("", Text(form, "textBoxLimitLower"));
                    Assert.Equal("0.5", Text(form, "textBoxLimitUpper"));
                    Assert.Equal(Manual, Text(form, "labelLimitSource"));
                }
            }
        }

        [Fact]
        public void MissingLimitTypedNextToTheOtherIsExported()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                joint.Limit.Lower = -0.25;
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, joint);
                    Enter(form, "textBoxLimitUpper", "0.5");
                    Enter(form, "textBoxLimitEffort", "10");
                    Enter(form, "textBoxLimitVelocity", "1");
                    form.SaveJointDataFromPropertyBoxes(joint);
                }
                XmlElement limit = ExportedLimit(joint);
                Assert.Equal("-0.25", limit.GetAttribute("lower"));
                Assert.Equal("0.5", limit.GetAttribute("upper"));
            }
        }

        [Fact]
        public void LimitSetOnOneSideDoesNotClaimAMate()
        {
            foreach (string type in LimitedTypes)
            {
                // The missing value must not be taken for the 0 the mate has on that side.
                Joint joint = NewJoint(type);
                joint.Limit.Lower = -0.25;
                joint.LimitInfo = new LimitMateInfo { MateName = "Limit1", Lower = -0.25, Upper = 0 };
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, joint);
                    Assert.Equal(Manual, Text(form, "labelLimitSource"));
                }
            }
        }

        [Fact]
        public void LimitsFromAMateStillNameTheMate()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                joint.Limit.Lower = -0.01;
                joint.Limit.Upper = 0.02;
                joint.Limit.Effort = 10;
                joint.Limit.Velocity = 1;
                joint.LimitInfo = new LimitMateInfo { MateName = "Limit1", Lower = -0.01, Upper = 0.02 };
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, joint);
                    Assert.Contains("Limit1", Text(form, "labelLimitSource"));
                    form.SaveJointDataFromPropertyBoxes(joint);
                }
                XmlElement limit = ExportedLimit(joint);
                Assert.Equal("-0.01", limit.GetAttribute("lower"));
                Assert.Equal("0.02", limit.GetAttribute("upper"));
            }
        }

        [Fact]
        public void JointsWithoutARangeShowNoSource()
        {
            foreach (string type in new[] { "continuous", "fixed" })
            {
                using (AssemblyExportForm form = NewForm())
                {
                    FillWholePage(form, NewJoint(type));
                    Assert.Equal("", Text(form, "labelLimitSource"));
                }
            }
        }
    }
}
