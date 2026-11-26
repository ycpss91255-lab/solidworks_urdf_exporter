using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A fixed joint exports no limits and a continuous joint no lower and upper
    /// limit, but the joint keeps them, so looking at another type on the joint page does not lose them
    /// (issue #193). A blank box of these joints clears its value. The page is filled and saved by the code
    /// of the form.
    /// </summary>
    public class TestLimitsByJointType
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] BoxNames =
            { "textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitEffort", "textBoxLimitVelocity" };

        private static readonly string[] Attributes = { "lower", "upper", "effort", "velocity" };

        private static readonly double?[] Values = { -Math.PI / 2, 1.0 / 3, 10, 2 };

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Members).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        private static Joint NewJoint(string name, string type, params double?[] limits)
        {
            Link parent = new Link { Name = "base_link" };
            Link child = new Link(parent) { Name = name + "_link" };
            child.Joint.Name = name;
            child.Joint.Type = type;
            child.Joint.Parent.Name = parent.Name;
            child.Joint.Child.Name = child.Name;
            child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
            child.Joint.CoordinateSystemName = "Origin_global";
            child.Joint.AxisName = "Axis_1";
            for (int i = 0; i < limits.Length; i++)
            {
                if (limits[i].HasValue)
                {
                    typeof(Limit).GetProperty(Attributes[i], Members | BindingFlags.IgnoreCase)
                        .SetValue(child.Joint.Limit, limits[i].Value);
                }
            }
            return child.Joint;
        }

        // The values the joint holds, also the ones that are not exported.
        private static double?[] Held(Joint joint)
        {
            return Attributes.Select(name => (double?)Field<URDFAttribute>(
                joint.Limit, char.ToUpperInvariant(name[0]) + name.Substring(1) + "Attribute").Value).ToArray();
        }

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
            SetField(form, "jointBoxes", new[] { "textBoxJointName", "comboBoxAxis", "comboBoxJointType" }
                .Concat(BoxNames).Select(name => Field<Control>(form, name)).ToArray());
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

        private static TextBox Box(AssemblyExportForm form, int index)
        {
            return Field<TextBox>(form, BoxNames[index]);
        }

        // The user chooses a type on the page of the joint and leaves the page.
        private static void ChooseType(AssemblyExportForm form, Joint joint, string type)
        {
            form.FillJointPropertyBoxes(joint);
            Field<ComboBox>(form, "comboBoxJointType").Text = type;
            form.SaveJointDataFromPropertyBoxes(joint);
            Assert.Equal(type, joint.Type);
            Joint other = NewJoint("other_joint", "revolute", 5, 6, 7, 8);
            form.FillJointPropertyBoxes(other);
            form.SaveJointDataFromPropertyBoxes(other);
        }

        // The limits of the joint in the exported robot: null without a limit element, "" for a missing one.
        private static string[] Exported(Joint joint, bool xacro = false)
        {
            Robot robot = new Robot { Name = "demo" };
            Link arm = new Link(robot.BaseLink) { Name = "arm" };
            robot.BaseLink.Name = "base_link";
            arm.Joint.SetElement(joint);
            robot.BaseLink.Children.Add(arm);
            robot.WriteXacro = xacro;
            StringBuilder text = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(text))
            {
                robot.WriteURDF(writer);
            }
            XmlDocument document = new XmlDocument();
            document.LoadXml(text.ToString());
            XmlNodeList limits = document.GetElementsByTagName("limit");
            return limits.Count == 0 ? null
                : Attributes.Select(name => ((XmlElement)limits[0]).GetAttribute(name)).ToArray();
        }

        private static string[] Text(params double?[] values)
        {
            return values.Select(v => v.HasValue ? v.Value.ToString(URDFAttribute.URDFNumberFormat) : "").ToArray();
        }

        [Fact]
        public void FixedJointExportsNoLimits()
        {
            foreach (bool xacro in new[] { false, true })
            {
                Joint joint = NewJoint("arm_joint", "fixed", Values);
                Assert.Null(Exported(joint, xacro));
                Assert.Equal(Values, Held(joint));
            }
        }

        [Fact]
        public void ContinuousJointExportsEffortAndVelocityOnly()
        {
            foreach (bool xacro in new[] { false, true })
            {
                Joint joint = NewJoint("arm_joint", "continuous", Values);
                Assert.Equal(Text(null, null, 10, 2), Exported(joint, xacro));
                Assert.Equal(Values, Held(joint));
            }
        }

        [Fact]
        public void JointsWithARangeExportAllLimits()
        {
            foreach (string type in new[] { "revolute", "prismatic" })
            {
                foreach (bool xacro in new[] { false, true })
                {
                    Assert.Equal(Text(Values), Exported(NewJoint("arm_joint", type, Values), xacro));
                }
            }
        }

        [Fact]
        public void LimitsThatAreNotExportedAreNotMissing()
        {
            // Effort and velocity were never filled in, for example limits from a mate.
            foreach (string type in new[] { "fixed", "continuous" })
            {
                Joint joint = NewJoint("arm_joint", type, -1, 1);
                Assert.True(joint.AreRequiredFieldsSatisfied());
                Assert.Null(Exported(joint));
            }
            Assert.True(NewJoint("arm_joint", "fixed", null, null, 10).AreRequiredFieldsSatisfied());
            Assert.Null(Exported(NewJoint("arm_joint", "continuous")));
            Assert.False(NewJoint("arm_joint", "continuous", null, null, 10).AreRequiredFieldsSatisfied());
            Assert.False(NewJoint("arm_joint", "revolute", -1, 1).AreRequiredFieldsSatisfied());
        }

        [Fact]
        public void TypeChangedOnAJointIsExportedByItsPresentType()
        {
            Joint joint = NewJoint("arm_joint", "fixed", Values);
            Assert.Null(Exported(joint));
            Assert.True(joint.AreRequiredFieldsSatisfied());
            joint.Type = "revolute";
            Assert.Equal(Text(Values), Exported(joint));
            joint.Type = "continuous";
            Assert.Equal(Text(null, null, 10, 2), Exported(joint));
        }

        [Fact]
        public void LookingAtAnotherTypeKeepsTheLimits()
        {
            foreach (string type in new[] { "revolute", "prismatic" })
            {
                foreach (string[] others in new[]
                {
                    new[] { "continuous" }, new[] { "fixed" }, new[] { "fixed", "continuous", "fixed" }
                })
                {
                    Joint joint = NewJoint("arm_joint", type, Values);
                    using (AssemblyExportForm form = NewForm())
                    {
                        foreach (string other in others)
                        {
                            ChooseType(form, joint, other);
                            Assert.Equal(Values, Held(joint));
                            // Shown, and left again without a change.
                            ChooseType(form, joint, other);
                            Assert.Equal(Values, Held(joint));
                        }
                        ChooseType(form, joint, type);
                    }
                    Assert.Equal(Values, Held(joint));
                    Assert.Equal(Text(Values), Exported(joint));
                }
            }
        }

        [Fact]
        public void BlankBoxClearsItsValue()
        {
            foreach (string type in new[] { "continuous", "fixed" })
            {
                for (int i = 0; i < BoxNames.Length; i++)
                {
                    Joint joint = NewJoint("arm_joint", type, Values);
                    using (AssemblyExportForm form = NewForm())
                    {
                        form.FillJointPropertyBoxes(joint);
                        Box(form, i).Text = i % 2 == 0 ? "" : " ";
                        form.SaveJointDataFromPropertyBoxes(joint);
                    }
                    double?[] expected = (double?[])Values.Clone();
                    expected[i] = null;
                    Assert.Equal(expected, Held(joint));
                }
            }
        }

        [Fact]
        public void AllBoxesBlankClearAllValues()
        {
            foreach (string type in new[] { "continuous", "fixed" })
            {
                Joint joint = NewJoint("arm_joint", type, Values);
                using (AssemblyExportForm form = NewForm())
                {
                    form.FillJointPropertyBoxes(joint);
                    for (int i = 0; i < BoxNames.Length; i++)
                    {
                        Box(form, i).Text = "";
                    }
                    form.SaveJointDataFromPropertyBoxes(joint);
                    Assert.Equal(new double?[4], Held(joint));
                    Assert.Null(Exported(joint));
                    // Filled and saved again: nothing comes back.
                    form.FillJointPropertyBoxes(joint);
                    form.SaveJointDataFromPropertyBoxes(joint);
                }
                Assert.Equal(new double?[4], Held(joint));
            }
        }

        [Fact]
        public void TypedValuesOfAContinuousJointAreExported()
        {
            Joint joint = NewJoint("arm_joint", "continuous");
            using (AssemblyExportForm form = NewForm())
            {
                form.FillJointPropertyBoxes(joint);
                Box(form, 2).Text = "0";
                form.SaveJointDataFromPropertyBoxes(joint);
                Assert.Equal(new double?[] { null, null, 0, null }, Held(joint));
                Assert.False(joint.AreRequiredFieldsSatisfied());
                Box(form, 3).Text = "1.5";
                form.SaveJointDataFromPropertyBoxes(joint);
            }
            Assert.Equal(new[] { "", "", "0", "1.5" }, Exported(joint));
        }

        [Fact]
        public void PageOfAFixedJointShowsTheLimitsItKeeps()
        {
            using (AssemblyExportForm form = NewForm())
            {
                form.FillJointPropertyBoxes(NewJoint("arm_joint", "fixed", -0.5, 0.25, 10, 2));
                Assert.Equal(new[] { "-0.5", "0.25", "10", "2" }, Enumerable.Range(0, 4).Select(i => Box(form, i).Text));
                form.FillJointPropertyBoxes(NewJoint("arm_joint", "fixed"));
                Assert.Equal(new[] { "", "", "", "" }, Enumerable.Range(0, 4).Select(i => Box(form, i).Text));
            }
        }

        private static object InvokeSerialization(string method, object argument)
        {
            return typeof(ConfigurationSerialization).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new[] { argument });
        }

        [Fact]
        public void SavedConfigurationAndCsvKeepTheLimits()
        {
            foreach (string type in new[] { "continuous", "fixed" })
            {
                Joint joint = NewJoint("arm_joint", type, Values);
                Assert.True(joint.AreRequiredFieldsSatisfied());

                Link link = new Link { Name = "arm" };
                link.Joint.SetElement(joint);
                OrderedDictionary csv = new OrderedDictionary();
                link.AppendToCSVDictionary(new List<string>(), csv);
                Assert.Equal(Values, Attributes.Select(name => (double?)csv["Link.Joint.Limit." + name]));

                string data = (string)InvokeSerialization("SerializeToString", new LinkNode(link));
                Link loaded = ((LinkNode)InvokeSerialization("DeserializeFromString", data)).RebuildLink();
                Assert.Equal(Values, Held(loaded.Joint));
                Assert.Equal(type == "fixed" ? null : Text(null, null, 10, 2), Exported(loaded.Joint));
            }
        }
    }
}
