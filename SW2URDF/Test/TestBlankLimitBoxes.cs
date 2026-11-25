using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using System.Xml;
using SW2URDF.UI;
using SW2URDF.URDF;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. A limit box that is left blank for a revolute or prismatic joint stays
    /// unset, so the joint is named in the message about missing required fields (issue #189). The boxes
    /// are saved and the joints are checked by the code of the form.
    /// </summary>
    public class TestBlankLimitBoxes
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] LimitedTypes = { "revolute", "prismatic" };

        private static readonly string[] BoxNames =
            { "textBoxLimitLower", "textBoxLimitUpper", "textBoxLimitEffort", "textBoxLimitVelocity" };

        private static readonly string[] AttributeNames = { "lower", "upper", "effort", "velocity" };

        private static T Field<T>(AssemblyExportForm form, string name)
        {
            return (T)typeof(AssemblyExportForm).GetField(name, Members).GetValue(form);
        }

        private static TextBox Box(AssemblyExportForm form, int index)
        {
            return Field<TextBox>(form, BoxNames[index]);
        }

        // The assembly form without its constructor, which needs SolidWorks. It is never shown.
        private static AssemblyExportForm NewForm(Joint joint, string format = "R")
        {
            AssemblyExportForm form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            typeof(AssemblyExportForm).GetField("ui", Members).SetValue(form, new UiText("english"));
            typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
            ComboBox types = Field<ComboBox>(form, "comboBoxJointType");
            types.Items.Clear();
            types.Items.AddRange(Joint.AvailableTypes.Cast<object>().ToArray());
            // The boxes hold radians here, also in a build where the form can show degrees.
            MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
            if (setUnit != null)
            {
                setUnit.Invoke(form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
            }
            joint.FillBoxes(Field<TextBox>(form, "textBoxJointName"), types);
            joint.Parent.FillBoxes(Field<Label>(form, "labelParent"));
            joint.Child.FillBoxes(Field<Label>(form, "labelChild"));
            joint.Limit.FillBoxes(Box(form, 0), Box(form, 1), Box(form, 2), Box(form, 3), format);
            return form;
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
            return child.Joint;
        }

        private static Joint LimitedJoint(string type)
        {
            Joint joint = NewJoint(type);
            joint.Limit.Lower = -0.5;
            joint.Limit.Upper = 0.25;
            joint.Limit.Effort = 10;
            joint.Limit.Velocity = 2;
            return joint;
        }

        // The names the form lists in the message about missing required fields.
        private static string MissingFields(AssemblyExportForm form, Joint joint)
        {
            Link link = new Link { Name = "arm" };
            link.Joint.SetElement(joint);
            LinkNode node = new LinkNode { Link = link };
            TreeView tree = Field<TreeView>(form, "treeViewJointTree");
            tree.Nodes.Clear();
            tree.Nodes.Add(node);
            return (string)typeof(AssemblyExportForm).GetMethod("CheckJointsForErrors", Members, null, Type.EmptyTypes, null)
                .Invoke(form, null);
        }

        private static void CheckMissing(AssemblyExportForm form, Joint joint)
        {
            form.SaveJointDataFromPropertyBoxes(joint);
            Assert.Contains("arm_joint", MissingFields(form, joint));
            Assert.False(joint.AreRequiredFieldsSatisfied());
        }

        private static string[] Exported(AssemblyExportForm form, Joint joint)
        {
            form.SaveJointDataFromPropertyBoxes(joint);
            Assert.Equal("", MissingFields(form, joint));
            StringWriter text = new StringWriter();
            using (XmlWriter writer = XmlWriter.Create(text))
            {
                joint.WriteURDF(writer);
            }
            XmlDocument document = new XmlDocument();
            document.LoadXml(text.ToString());
            XmlElement limit = (XmlElement)document.SelectSingleNode("joint/limit");
            return limit == null ? null : AttributeNames.Select(name => limit.GetAttribute(name)).ToArray();
        }

        [Fact]
        public void AllBoxesBlankIsReported()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                using (AssemblyExportForm form = NewForm(joint))
                {
                    CheckMissing(form, joint);
                    Assert.False(joint.Limit.ElementContainsData());
                    // Saved again: nothing is filled in by then either.
                    CheckMissing(form, joint);
                }
            }
        }

        [Fact]
        public void OneBlankBoxNextToTypedOnesIsReported()
        {
            foreach (string type in LimitedTypes)
            {
                for (int blank = 0; blank < BoxNames.Length; blank++)
                {
                    Joint joint = NewJoint(type);
                    using (AssemblyExportForm form = NewForm(joint))
                    {
                        for (int i = 0; i < BoxNames.Length; i++)
                        {
                            Box(form, i).Text = i == blank ? " " : "0.5";
                        }
                        CheckMissing(form, joint);
                        Box(form, blank).Text = "0.5";
                        Assert.Equal(new[] { "0.5", "0.5", "0.5", "0.5" }, Exported(form, joint));
                    }
                }
            }
        }

        [Fact]
        public void BlankEffortAndVelocityOfLimitsFromAMateAreReported()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                joint.Limit.Lower = -0.5;
                joint.Limit.Upper = 0.25;
                using (AssemblyExportForm form = NewForm(joint))
                {
                    CheckMissing(form, joint);
                    Box(form, 2).Text = "10";
                    CheckMissing(form, joint);
                    Box(form, 3).Text = "2";
                    Assert.Equal(new[] { "-0.5", "0.25", "10", "2" }, Exported(form, joint));
                }
            }
        }

        [Fact]
        public void ClearedBoxIsReported()
        {
            foreach (string type in LimitedTypes)
            {
                for (int i = 0; i < BoxNames.Length; i++)
                {
                    Joint joint = LimitedJoint(type);
                    using (AssemblyExportForm form = NewForm(joint))
                    {
                        Box(form, i).Text = "";
                        CheckMissing(form, joint);
                    }
                }
            }
        }

        [Fact]
        public void TypedZeroIsAValue()
        {
            foreach (string type in LimitedTypes)
            {
                Joint joint = NewJoint(type);
                using (AssemblyExportForm form = NewForm(joint))
                {
                    for (int i = 0; i < BoxNames.Length; i++)
                    {
                        Box(form, i).Text = "0";
                    }
                    Assert.Equal(new[] { "0", "0", "0", "0" }, Exported(form, joint));
                }
            }
        }

        [Fact]
        public void UneditedValuesStayExact()
        {
            // Also when the box shows fewer digits than the value has.
            foreach (string format in new[] { "R", "G12" })
            {
                foreach (string type in LimitedTypes)
                {
                    Joint joint = LimitedJoint(type);
                    joint.Limit.Lower = -Math.PI / 2;
                    joint.Limit.Upper = 0.019999999999999997;
                    joint.Limit.Velocity = 0;
                    using (AssemblyExportForm form = NewForm(joint, format))
                    {
                        form.SaveJointDataFromPropertyBoxes(joint);
                        form.SaveJointDataFromPropertyBoxes(joint);
                    }
                    Assert.Equal(-Math.PI / 2, joint.Limit.Lower);
                    Assert.Equal(0.019999999999999997, joint.Limit.Upper);
                    Assert.Equal(10, joint.Limit.Effort);
                    Assert.Equal(0, joint.Limit.Velocity);
                }
            }
        }

        [Fact]
        public void JointsThatNeedNoLimitsAreSavedAsBefore()
        {
            Joint joint = NewJoint("continuous");
            using (AssemblyExportForm form = NewForm(joint))
            {
                Assert.Null(Exported(form, joint));
                Box(form, 2).Text = "10";
                Assert.Equal(new[] { "", "", "10", "0" }, Exported(form, joint));
            }
            joint = NewJoint("fixed");
            using (AssemblyExportForm form = NewForm(joint))
            {
                Assert.Null(Exported(form, joint));
            }
            // Limits of a hinge that is made fixed on the page, with effort and velocity never filled in.
            joint = NewJoint("revolute");
            joint.Limit.Lower = -0.5;
            joint.Limit.Upper = 0.25;
            using (AssemblyExportForm form = NewForm(joint))
            {
                Field<ComboBox>(form, "comboBoxJointType").Text = "fixed";
                Assert.Equal(new[] { "-0.5", "0.25", "0", "0" }, Exported(form, joint));
            }
        }

        [Fact]
        public void OtherBoxesAreSavedAsBefore()
        {
            Joint joint = LimitedJoint("revolute");
            using (AssemblyExportForm form = NewForm(joint))
            {
                // A required value of the safety controller that is left blank next to a typed one is 0.
                Field<TextBox>(form, "textBoxKPosition").Text = "3";
                form.SaveJointDataFromPropertyBoxes(joint);
            }
            Assert.Equal(3, joint.Safety.KPosition);
            Assert.Equal(0, joint.Safety.KVelocity);
            Assert.Equal(new double[] { 0, 0, 0 }, joint.Origin.GetXYZ());
        }
    }
}
