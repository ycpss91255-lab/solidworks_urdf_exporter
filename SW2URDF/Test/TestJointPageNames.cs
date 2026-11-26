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
    /// Does not need SolidWorks. Showing the joint page does not change the names of the coordinate system,
    /// the axis and the mimic joint, a mimic follows the renaming of the joint it names, and a mimic that
    /// names no joint is reported (issue #196). The page is filled, saved and checked by the code of the form.
    /// </summary>
    public class TestJointPageNames
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] AxisBoxNames = { "textBoxAxisX", "textBoxAxisY", "textBoxAxisZ" };

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Members).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        // A robot whose joints hang on the base link.
        private sealed class Page : IDisposable
        {
            public readonly AssemblyExportForm Form;
            private readonly Link baseLink = new Link { Name = "base_link" };

            // The assembly form and its exporter without their constructors, which need SolidWorks. The form
            // is never shown.
            public Page(params string[] jointNames)
            {
                ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                SetField(exporter, "ReferenceCoordinateSystemNames", new List<string> { "Origin_1", "Origin_2" });
                SetField(exporter, "ReferenceAxesNames", new List<string> { "Axis_1", "Axis_2" });
                Robot robot = new Robot();
                robot.SetBaseLink(baseLink);
                exporter.URDFRobot = robot;

                Form = (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm));
                typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(Form, null);
                SetField(Form, "ui", new UiText("english"));
                typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(Form, null);
                // With a window each, the boxes clear their text with their list as they do on the screen.
                foreach (string name in new[] { "comboBoxOrigin", "comboBoxAxis", "MimicJointComboBox" })
                {
                    Assert.NotEqual(IntPtr.Zero, Box(name).Handle);
                }
                SetField(Form, "jointBoxes", new[]
                {
                    "textBoxJointName", "comboBoxAxis", "comboBoxJointType", "textBoxLimitLower", "textBoxLimitUpper",
                    "textBoxLimitEffort", "textBoxLimitVelocity"
                }.Concat(AxisBoxNames).Select(name => Field<Control>(Form, name)).ToArray());
                ComboBox types = Box("comboBoxJointType");
                types.Items.Clear();
                types.Items.AddRange(Joint.AvailableTypes.Cast<object>().ToArray());
                Form.Exporter = exporter;
                MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
                if (setUnit != null)
                {
                    setUnit.Invoke(Form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
                }

                TreeView tree = Field<TreeView>(Form, "treeViewJointTree");
                foreach (string name in jointNames)
                {
                    Link child = new Link(baseLink) { Name = name + "_link" };
                    child.Joint.Name = name;
                    child.Joint.Type = "revolute";
                    child.Joint.Parent.Name = baseLink.Name;
                    child.Joint.Child.Name = child.Name;
                    child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
                    child.Joint.CoordinateSystemName = "Origin_1";
                    child.Joint.AxisName = "Axis_1";
                    child.Joint.Limit.Lower = -1;
                    child.Joint.Limit.Upper = 1;
                    child.Joint.Limit.Effort = 10;
                    child.Joint.Limit.Velocity = 2;
                    baseLink.Children.Add(child);
                    tree.Nodes.Add(new LinkNode { Link = child });
                }
            }

            public ComboBox Box(string name)
            {
                return Field<ComboBox>(Form, name);
            }

            public Joint this[int index] => baseLink.Children[index].Joint;

            // The user shows the page of the joint, changes something and leaves the page.
            public void Edit(Joint joint, Action change = null)
            {
                Form.FillJointPropertyBoxes(joint);
                change?.Invoke();
                Form.SaveJointDataFromPropertyBoxes(joint);
            }

            public void Rename(Joint joint, string name)
            {
                Edit(joint, () => Field<TextBox>(Form, "textBoxJointName").Text = name);
                Assert.Equal(name, joint.Name);
            }

            // The joints the form lists in the message about missing required fields.
            public string Problems()
            {
                return (string)typeof(AssemblyExportForm)
                    .GetMethod("CheckJointsForErrors", Members, null, Type.EmptyTypes, null).Invoke(Form, null);
            }

            public void Dispose()
            {
                Form.Dispose();
            }
        }

        [Fact]
        public void CoordinateSystemNameThatIsNotListedIsKept()
        {
            using (Page page = new Page("arm_joint", "other_joint"))
            {
                page[0].CoordinateSystemName = "Origin_gone";
                page.Edit(page[0]);
                page.Edit(page[1]);
                page.Edit(page[0]);
                Assert.Equal("Origin_gone", page[0].CoordinateSystemName);
                Assert.Equal("Origin_1", page[1].CoordinateSystemName);
            }
        }

        [Fact]
        public void AxisNameThatIsNotListedIsKept()
        {
            foreach (string name in new[] { "Axis_gone", "Automatically Generate", "", null })
            {
                using (Page page = new Page("arm_joint", "other_joint"))
                {
                    page[0].Type = "fixed";
                    page[0].AxisName = name;
                    page.Edit(page[1]);
                    page.Edit(page[0]);
                    page.Edit(page[1]);
                    page.Edit(page[0]);
                    Assert.Equal(name, page[0].AxisName);
                    Assert.Equal("Axis_1", page[1].AxisName);
                }
            }
        }

        [Fact]
        public void ChosenCoordinateSystemAndAxisAreSaved()
        {
            using (Page page = new Page("arm_joint"))
            {
                page[0].CoordinateSystemName = "Origin_gone";
                page[0].AxisName = "Axis_gone";
                page.Edit(page[0], () =>
                {
                    // Without SolidWorks the axis cannot be measured.
                    SetField(page.Form, "AutoUpdatingForm", true);
                    page.Box("comboBoxOrigin").SelectedIndex = 1;
                    page.Box("comboBoxAxis").SelectedIndex = 1;
                });
                Assert.Equal("Origin_2", page[0].CoordinateSystemName);
                Assert.Equal("Axis_2", page[0].AxisName);
            }
        }

        [Fact]
        public void MimicJointThatIsNotListedIsShownAndKept()
        {
            // The second one differs from a joint of the robot by case only.
            foreach (string name in new[] { "joint_gone", "Other_Joint" })
            {
                using (Page page = new Page("arm_joint", "other_joint"))
                {
                    page[0].Mimic.Update(name, "0.5", "0.25");
                    page.Form.FillJointPropertyBoxes(page[0]);
                    Assert.Equal(name, page.Box("MimicJointComboBox").Text);
                    page.Form.SaveJointDataFromPropertyBoxes(page[0]);
                    page.Edit(page[1]);
                    page.Edit(page[0]);
                    Assert.Equal(name, page[0].Mimic.JointName);
                    Assert.Equal(0.5, page[0].Mimic.Multiplier);
                    Assert.Equal(0.25, page[0].Mimic.Offset);
                    Assert.False(page[1].Mimic.ElementContainsData());
                }
            }
        }

        [Fact]
        public void ListedMimicJointIsKeptAndCanBeChanged()
        {
            using (Page page = new Page("arm_joint", "other_joint", "third_joint", "Third_joint"))
            {
                page[0].Mimic.Update("Third_joint", "1", "0");
                page.Edit(page[0]);
                Assert.Equal("Third_joint", page[0].Mimic.JointName);
                page.Edit(page[0], () => page.Box("MimicJointComboBox").SelectedIndex = 1);
                Assert.Equal("other_joint", page[0].Mimic.JointName);
                Assert.Equal("", page.Problems());
                page.Edit(page[0], () => Field<CheckBox>(page.Form, "MimicCheckBox").Checked = false);
                Assert.False(page[0].Mimic.ElementContainsData());
            }
        }

        [Fact]
        public void MimicFollowsTheRenamingOfItsJoint()
        {
            using (Page page = new Page("arm_joint", "other_joint", "third_joint"))
            {
                page[0].Mimic.Update("other_joint", "0.5", "0.25");
                page[2].Mimic.Update("arm_joint", "1", "0");
                // Also when the page of the mimicking joint was shown before, and not shown again.
                page.Edit(page[0]);
                page.Rename(page[1], "new_joint");
                Assert.Equal("new_joint", page[0].Mimic.JointName);
                Assert.Equal("arm_joint", page[2].Mimic.JointName);
                page.Edit(page[0]);
                Assert.Equal("new_joint", page[0].Mimic.JointName);
                Assert.Equal(0.5, page[0].Mimic.Multiplier);
                Assert.Equal("", page.Problems());

                // Renamed in two steps, with a blank name in between.
                page.Rename(page[1], "");
                page.Rename(page[1], "final_joint");
                Assert.Equal("final_joint", page[0].Mimic.JointName);
            }
        }

        [Fact]
        public void SwappedNamesKeepEveryMimicOnItsJoint()
        {
            using (Page page = new Page("left_joint", "right_joint", "left_mimic", "right_mimic"))
            {
                page[2].Mimic.Update("left_joint", "1", "0");
                page[3].Mimic.Update("right_joint", "1", "0");
                // For a moment both joints have the same name.
                page.Rename(page[0], "right_joint");
                page.Rename(page[1], "left_joint");
                Assert.Equal("right_joint", page[2].Mimic.JointName);
                Assert.Equal("left_joint", page[3].Mimic.JointName);
            }
        }

        [Fact]
        public void MimicThatWasChangedDoesNotFollowItsOldJoint()
        {
            using (Page page = new Page("arm_joint", "other_joint", "third_joint"))
            {
                page[0].Mimic.Update("other_joint", "1", "0");
                page.Edit(page[0]);
                page.Edit(page[0], () => page.Box("MimicJointComboBox").SelectedIndex = 2);
                page.Rename(page[1], "new_joint");
                Assert.Equal("third_joint", page[0].Mimic.JointName);
                page.Rename(page[2], "last_joint");
                Assert.Equal("last_joint", page[0].Mimic.JointName);
            }
        }

        [Fact]
        public void MimicThatNamesNoJointIsReported()
        {
            foreach (string name in new[] { "", " ", "joint_gone", "Other_joint" })
            {
                using (Page page = new Page("arm_joint", "other_joint"))
                {
                    page[0].Mimic.Update(name, "1", "0");
                    string problems = page.Problems();
                    Assert.Contains("arm_joint", problems);
                    Assert.DoesNotContain("other_joint\r", problems);
                    if (name.Trim().Length > 0)
                    {
                        Assert.Contains(name, problems);
                    }
                    // Still reported after its page was shown.
                    page.Edit(page[0]);
                    Assert.Contains("arm_joint", page.Problems());
                    page.Edit(page[0], () => page.Box("MimicJointComboBox").SelectedIndex = 1);
                    Assert.Equal("other_joint", page[0].Mimic.JointName);
                    Assert.Equal("", page.Problems());
                }
            }
        }

        [Fact]
        public void ReportOfAMimicIsTranslated()
        {
            const string key = "{0}: the joint it mimics, \"{1}\", is not in the robot";
            Assert.Equal(key, new UiText("english").Get(key));
            Assert.NotEqual(key, new UiText("chinese").Get(key));
        }
    }
}
