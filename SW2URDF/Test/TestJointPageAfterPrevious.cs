using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. After Previous on the link page a joint is selected, and what is typed for
    /// it is saved (issue #203). The trees have windows, so that they raise their events as on the screen,
    /// but the form is never shown.
    /// </summary>
    public class TestJointPageAfterPrevious
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo Find(object target, string name)
        {
            return (target is AssemblyExportForm ? typeof(AssemblyExportForm) : target.GetType()).GetField(name, Members);
        }

        private static T Field<T>(object target, string name)
        {
            return (T)Find(target, name).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            Find(target, name).SetValue(target, value);
        }

        private class SilentForm : AssemblyExportForm
        {
            // Never called, the form is made without a constructor.
            private SilentForm() : base(null, null, null)
            {
            }

            // The efforts of the joints when the configuration was to be saved.
            public double[] SavedEfforts;

            public override void SaveConfigTree(ModelDoc2 model, LinkNode baseNode, bool warnUser)
            {
                SavedEfforts = baseNode.Nodes.Cast<LinkNode>().Select(node => node.Link.Joint.Limit.Effort).ToArray();
            }
        }

        // Only a safeguard: keeps the dialogs of this thread from being created, and counts them.
        private sealed class NoDialogs : IDisposable
        {
            private const int WH_CBT = 5;
            private const int HCBT_CREATEWND = 3;
            private readonly HookProc proc;
            private readonly IntPtr hook;

            public int Count;

            public NoDialogs()
            {
                proc = Handle;
                hook = SetWindowsHookEx(WH_CBT, proc, IntPtr.Zero, GetCurrentThreadId());
                Assert.NotEqual(IntPtr.Zero, hook);
            }

            private IntPtr Handle(int code, IntPtr window, IntPtr data)
            {
                StringBuilder name = new StringBuilder(64);
                if (code == HCBT_CREATEWND && GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770")
                {
                    Count++;
                    return new IntPtr(1);
                }
                return CallNextHookEx(hook, code, window, data);
            }

            public void Dispose()
            {
                UnhookWindowsHookEx(hook);
            }

            private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hook);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();
        }

        // A robot whose joints hang on the base link, with the joint page shown.
        private sealed class Pages : IDisposable
        {
            private readonly SilentForm form;
            private readonly LinkNode baseNode;
            private readonly NoDialogs noDialogs = new NoDialogs();
            private readonly List<LinkNode> nodes = new List<LinkNode>();

            // The assembly form and its exporter without their constructors, which need SolidWorks.
            public Pages(params string[] jointNames)
            {
                ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                SetField(exporter, "ReferenceCoordinateSystemNames", new List<string> { "Origin_1" });
                SetField(exporter, "ReferenceAxesNames", new List<string> { "Axis_1" });
                Link baseLink = new Link { Name = "base_link", SWComponents = new List<Component2>() };
                Robot robot = new Robot();
                robot.SetBaseLink(baseLink);
                exporter.URDFRobot = robot;

                form = (SilentForm)FormatterServices.GetUninitializedObject(typeof(SilentForm));
                typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
                SetField(form, "ui", new UiText("english"));
                typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
                SetField(form, "jointBoxes", new[]
                {
                    "textBoxJointName", "comboBoxAxis", "comboBoxJointType", "textBoxLimitLower", "textBoxLimitUpper",
                    "textBoxLimitEffort", "textBoxLimitVelocity", "textBoxAxisX", "textBoxAxisY", "textBoxAxisZ"
                }.Select(name => Field<Control>(form, name)).ToArray());
                SetField(form, "linkBoxes", new[] { "textBoxMass", "comboBoxMaterials", "textBoxTexture" }
                    .Select(name => Field<Control>(form, name)).ToArray());
                ComboBox types = Field<ComboBox>(form, "comboBoxJointType");
                types.Items.Clear();
                types.Items.AddRange(Joint.AvailableTypes.Cast<object>().ToArray());
                form.Exporter = exporter;
                MethodInfo setUnit = typeof(AssemblyExportForm).GetMethod("SetAngleUnit", Members);
                if (setUnit != null)
                {
                    setUnit.Invoke(form, new[] { Enum.Parse(setUnit.GetParameters()[0].ParameterType, "Radians") });
                }
                var selection = new Mock<SelectionMgr>();
                selection.Setup(manager => manager.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                var features = new Mock<FeatureManager>();
                features.Setup(manager => manager.GetFeatures(It.IsAny<bool>())).Returns(new object[0]);
                var model = new Mock<ModelDoc2>();
                model.Setup(document => document.SelectionManager).Returns(selection.Object);
                model.Setup(document => document.FeatureManager).Returns(features.Object);
                SetField(form, "ActiveSWModel", model.Object);

                baseLink.Inertial.Mass.Value = 100;
                baseNode = new LinkNode { Link = baseLink, IsBaseNode = true, Text = baseLink.Name };
                SetField(form, "BaseNode", baseNode);
                foreach (string name in jointNames)
                {
                    Link child = new Link(baseLink) { Name = name + "_link", SWComponents = new List<Component2>() };
                    child.Inertial.Mass.Value = nodes.Count + 1;
                    child.Joint.Name = name;
                    child.Joint.Type = "revolute";
                    child.Joint.Parent.Name = baseLink.Name;
                    child.Joint.Child.Name = child.Name;
                    child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
                    child.Joint.CoordinateSystemName = "Origin_1";
                    child.Joint.AxisName = "Axis_1";
                    child.Joint.Limit.Lower = -1;
                    child.Joint.Limit.Upper = 1;
                    child.Joint.Limit.Effort = 10 * (nodes.Count + 1);
                    child.Joint.Limit.Velocity = 2;
                    baseLink.Children.Add(child);
                    LinkNode node = new LinkNode { Link = child, Text = name };
                    nodes.Add(node);
                    baseNode.Nodes.Add(node);
                }
                foreach (TreeView tree in new[] { JointTree, LinkTree })
                {
                    Assert.NotEqual(IntPtr.Zero, tree.Handle);
                }
                form.FillJointTree();
            }

            public Link this[int index] => nodes[index].Link;

            public Link BaseLink => baseNode.Link;

            public TreeView JointTree => Field<TreeView>(form, "treeViewJointTree");

            public TreeView LinkTree => Field<TreeView>(form, "treeViewLinkProperties");

            public TextBox Box(string name)
            {
                return Field<TextBox>(form, name);
            }

            private void Call(string handler)
            {
                typeof(AssemblyExportForm).GetMethod(handler, Members).Invoke(form, new object[] { form, EventArgs.Empty });
            }

            public void SelectJoint(int index)
            {
                JointTree.SelectedNode = nodes[index];
                Assert.Equal(this[index].Joint.Name, Box("textBoxJointName").Text);
            }

            public void SelectLink(int index)
            {
                LinkTree.SelectedNode = nodes[index];
                Assert.Equal(this[index].Inertial.Mass.Value.ToString("R"), Box("textBoxMass").Text);
            }

            public void Next()
            {
                Call("ButtonJointNextClick");
                Assert.Empty(JointTree.Nodes);
                Assert.Equal(nodes.Count, baseNode.Nodes.Count);
            }

            public void Previous()
            {
                Call("ButtonLinksPreviousClick");
                Assert.Equal(nodes.Count, JointTree.Nodes.Count);
            }

            public double[] Cancel()
            {
                Call("Button_Joint_Cancel_Click");
                return form.SavedEfforts;
            }

            public double[] Efforts()
            {
                return nodes.Select(node => node.Link.Joint.Limit.Effort).ToArray();
            }

            public void Dispose()
            {
                form.Dispose();
                noDialogs.Dispose();
                Assert.Equal(0, noDialogs.Count);
            }
        }

        private static void ToLinkPageAndBack(Pages pages)
        {
            pages.SelectJoint(0);
            pages.Next();
            pages.Previous();
        }

        [Fact]
        public void TypedAfterPreviousIsSavedForTheOnlyJoint()
        {
            using (Pages pages = new Pages("arm_joint"))
            {
                ToLinkPageAndBack(pages);
                pages.Box("textBoxLimitEffort").Text = "77";
                pages.Next();
                Assert.Equal(new double[] { 77 }, pages.Efforts());
            }
        }

        [Fact]
        public void JointThatIsShownAfterPreviousIsTheSelectedOne()
        {
            using (Pages pages = new Pages("arm_joint", "other_joint", "third_joint"))
            {
                pages.SelectJoint(2);
                pages.Next();
                pages.Previous();
                LinkNode selected = (LinkNode)pages.JointTree.SelectedNode;
                Assert.NotNull(selected);
                Assert.Equal(selected.Link.Joint.Name, pages.Box("textBoxJointName").Text);
                Assert.Equal(selected.Link.Joint.Limit.Effort.ToString("R"), pages.Box("textBoxLimitEffort").Text);

                // Only this joint gets what is typed.
                pages.Box("textBoxLimitEffort").Text = "77";
                pages.Next();
                Assert.Equal(77, selected.Link.Joint.Limit.Effort);
                Assert.Equal(77 + 10 + 20 + 30, pages.Efforts().Sum() + (selected.Index + 1) * 10);
            }
        }

        [Fact]
        public void TypedAfterPreviousIsSavedWhenAnotherJointIsSelected()
        {
            using (Pages pages = new Pages("arm_joint", "other_joint"))
            {
                ToLinkPageAndBack(pages);
                int shown = pages.JointTree.SelectedNode == null ? 0 : pages.JointTree.SelectedNode.Index;
                pages.Box("textBoxLimitEffort").Text = "77";
                pages.SelectJoint(1 - shown);
                Assert.Equal(77, pages[shown].Joint.Limit.Effort);
                Assert.Equal((2 - shown) * 10, pages[1 - shown].Joint.Limit.Effort);
            }
        }

        [Fact]
        public void TypedAfterPreviousIsSavedByCancel()
        {
            using (Pages pages = new Pages("arm_joint"))
            {
                ToLinkPageAndBack(pages);
                pages.Box("textBoxLimitEffort").Text = "77";
                Assert.Equal(new double[] { 77 }, pages.Cancel());
            }
        }

        [Fact]
        public void LinkThatWasShownDoesNotGetTheBoxesOfTheJointPage()
        {
            using (Pages pages = new Pages("arm_joint", "other_joint"))
            {
                pages.SelectJoint(0);
                pages.Next();
                pages.SelectLink(1);
                pages.Previous();
                pages.Box("textBoxLimitEffort").Text = "77";
                int shown = pages.JointTree.SelectedNode == null ? 0 : pages.JointTree.SelectedNode.Index;
                pages.Next();
                Assert.Equal(77, pages[shown].Joint.Limit.Effort);
                Assert.Equal((2 - shown) * 10, pages[1 - shown].Joint.Limit.Effort);
                Assert.Equal(new[] { "arm_joint", "other_joint" }, new[] { pages[0].Joint.Name, pages[1].Joint.Name });
                Assert.NotEqual("arm_joint", pages.BaseLink.Joint.Name);
                Assert.NotEqual("other_joint", pages.BaseLink.Joint.Name);
            }
        }

        [Fact]
        public void LinksKeepTheirDataWhenGoingBackAndForth()
        {
            using (Pages pages = new Pages("arm_joint", "other_joint", "third_joint"))
            {
                for (int turn = 0; turn < 2; turn++)
                {
                    pages.SelectJoint(1);
                    pages.Next();
                    pages.SelectLink(2);
                    pages.SelectLink(0);
                    pages.Previous();
                }
                pages.Next();
                Assert.Equal(new double[] { 100, 1, 2, 3 },
                    new[] { pages.BaseLink, pages[0], pages[1], pages[2] }.Select(link => link.Inertial.Mass.Value));
                Assert.Equal(new double[] { 10, 20, 30 }, pages.Efforts());
            }
        }
    }
}