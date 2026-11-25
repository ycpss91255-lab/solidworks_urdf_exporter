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
    /// Does not need SolidWorks. After the message about missing required fields, what is typed for the
    /// joint that is still shown is saved (issue #199). The handlers of the form are called as its buttons
    /// and its tree call them. The message is counted and the configuration is not written, so that no
    /// window is shown.
    /// </summary>
    public class TestJointPageAfterErrors
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

            public int Messages;

            // The limits of the first joint when the configuration was to be saved.
            public double[] SavedLimits;

            internal override void ShowJointErrors(string message)
            {
                Messages++;
            }

            public override void SaveConfigTree(ModelDoc2 model, LinkNode baseNode, bool warnUser)
            {
                Limit limit = ((LinkNode)baseNode.Nodes[0]).Link.Joint.Limit;
                SavedLimits = new[] { limit.Effort, limit.Velocity };
            }
        }

        // Only a safeguard: keeps the dialogs of this thread from being created, and counts them.
        private sealed class DialogCounter : IDisposable
        {
            private const int WH_CBT = 5;
            private const int HCBT_CREATEWND = 3;
            private readonly HookProc proc;
            private readonly IntPtr hook;

            public int Count;

            public DialogCounter()
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

        // The joint page of a robot whose joints hang on the base link. No effort and velocity are set.
        private sealed class Page : IDisposable
        {
            private readonly SilentForm form;
            private readonly LinkNode baseNode;
            private readonly DialogCounter dialogs = new DialogCounter();
            private readonly List<LinkNode> nodes = new List<LinkNode>();
            private readonly Dictionary<Joint, URDFElement> missing = new Dictionary<Joint, URDFElement>();

            // The assembly form and its exporter without their constructors, which need SolidWorks. The form
            // is never shown.
            public Page(params string[] jointNames)
            {
                ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                SetField(exporter, "ReferenceCoordinateSystemNames", new List<string> { "Origin_1" });
                SetField(exporter, "ReferenceAxesNames", new List<string> { "Axis_1" });
                Link baseLink = new Link { Name = "base_link" };
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
                var model = new Mock<ModelDoc2>();
                model.Setup(document => document.SelectionManager).Returns(selection.Object);
                SetField(form, "ActiveSWModel", model.Object);

                baseLink.SWComponents = new List<Component2>();
                baseNode = new LinkNode { Link = baseLink, IsBaseNode = true };
                SetField(form, "BaseNode", baseNode);
                foreach (string name in jointNames)
                {
                    Link child = new Link(baseLink) { Name = name + "_link", SWComponents = new List<Component2>() };
                    child.Joint.Name = name;
                    child.Joint.Type = "revolute";
                    child.Joint.Parent.Name = baseLink.Name;
                    child.Joint.Child.Name = child.Name;
                    child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
                    child.Joint.CoordinateSystemName = "Origin_1";
                    child.Joint.AxisName = "Axis_1";
                    child.Joint.Limit.Lower = -1;
                    child.Joint.Limit.Upper = 1;
                    baseLink.Children.Add(child);
                    LinkNode node = new LinkNode { Link = child };
                    nodes.Add(node);
                    baseNode.Nodes.Add(node);
                }
                form.FillJointTree();
            }

            public Joint this[int index] => nodes[index].Link.Joint;

            public LinkNode Selected => Field<LinkNode>(form, "previouslySelectedNode");

            public bool LinkPageShown => baseNode.Nodes.Count > 0;

            private void Call(string handler, EventArgs e)
            {
                typeof(AssemblyExportForm).GetMethod(handler, Members).Invoke(form, new object[] { form, e });
            }

            public void Select(int index)
            {
                Call("TreeViewJointtreeAfterSelect", new TreeViewEventArgs(nodes[index]));
            }

            public void Fill(string effort, string velocity)
            {
                Field<TextBox>(form, "textBoxLimitEffort").Text = effort;
                Field<TextBox>(form, "textBoxLimitVelocity").Text = velocity;
            }

            // Whether the message about missing required fields came.
            public bool NextIsStopped()
            {
                int before = form.Messages;
                Call("ButtonJointNextClick", EventArgs.Empty);
                Assert.Equal(LinkPageShown ? before : before + 1, form.Messages);
                return !LinkPageShown;
            }

            // Returns what was saved of the first joint: effort and velocity.
            public double[] Cancel()
            {
                Call("Button_Joint_Cancel_Click", EventArgs.Empty);
                return form.SavedLimits;
            }

            // Blank effort and velocity boxes are saved as 0 and not reported before issue #189 is fixed.
            // Then another field is missing, until it is filled.
            public void MissSomething(int index)
            {
                this[index].Limit.SetRequired(true);
                Limit probe = new Limit();
                probe.SetRequired(true);
                probe.SetValues(new TextBox { Text = "-1" }, new TextBox { Text = "1" }, new TextBox(), new TextBox());
                if (probe.AreRequiredFieldsSatisfied())
                {
                    URDFElement element = new URDFElement("missing", true);
                    Field<List<URDFAttribute>>(element, "Attributes").Add(new URDFAttribute("value", true, null));
                    Field<List<URDFElement>>(this[index], "ChildElements").Add(element);
                    missing[this[index]] = element;
                }
            }

            public void NothingMissing(int index)
            {
                if (missing.TryGetValue(this[index], out URDFElement element))
                {
                    Field<List<URDFElement>>(this[index], "ChildElements").Remove(element);
                }
            }

            public void Dispose()
            {
                form.Dispose();
                dialogs.Dispose();
                Assert.Equal(0, dialogs.Count);
            }
        }

        private static void AssertSaved(Joint joint, double effort, double velocity)
        {
            Assert.Equal(effort, joint.Limit.Effort);
            Assert.Equal(velocity, joint.Limit.Velocity);
        }

        [Fact]
        public void NextGoesOnWhenTheOnlyJointWasCorrected()
        {
            using (Page page = new Page("arm_joint"))
            {
                page.Select(0);
                page.MissSomething(0);
                Assert.True(page.NextIsStopped());
                page.Fill("10", "2");
                page.NothingMissing(0);
                Assert.False(page.NextIsStopped());
                AssertSaved(page[0], 10, 2);
                // The link page starts without a selected node.
                Assert.Null(page.Selected);
            }
        }

        [Fact]
        public void EveryMessageCanBeFollowedByACorrection()
        {
            using (Page page = new Page("arm_joint"))
            {
                page.Select(0);
                page.MissSomething(0);
                Assert.True(page.NextIsStopped());
                page.Fill("10", "");
                Assert.True(page.NextIsStopped());
                Assert.Equal(10, page[0].Limit.Effort);
                page.Fill("10", "2");
                page.NothingMissing(0);
                Assert.False(page.NextIsStopped());
                AssertSaved(page[0], 10, 2);
            }
        }

        [Fact]
        public void CancelAfterTheMessageSavesTheCorrection()
        {
            using (Page page = new Page("arm_joint"))
            {
                page.Select(0);
                page.MissSomething(0);
                Assert.True(page.NextIsStopped());
                page.Fill("10", "2");
                page.NothingMissing(0);
                Assert.Equal(new double[] { 10, 2 }, page.Cancel());
                AssertSaved(page[0], 10, 2);
            }
        }

        [Fact]
        public void SelectingAnotherJointAfterTheMessageSavesTheCorrection()
        {
            using (Page page = new Page("arm_joint", "other_joint"))
            {
                page.Select(1);
                page.Fill("5", "1");
                page.Select(0);
                page.MissSomething(0);
                Assert.True(page.NextIsStopped());
                page.Fill("10", "2");
                page.NothingMissing(0);
                page.Select(1);
                AssertSaved(page[0], 10, 2);
                AssertSaved(page[1], 5, 1);
                Assert.False(page.NextIsStopped());
                AssertSaved(page[0], 10, 2);
                AssertSaved(page[1], 5, 1);
            }
        }

        [Fact]
        public void CorrectionOfAnotherJointThanTheReportedOneIsSaved()
        {
            using (Page page = new Page("arm_joint", "other_joint"))
            {
                page.Select(0);
                page.Fill("10", "2");
                page.MissSomething(1);
                Assert.True(page.NextIsStopped());
                page.Fill("20", "4");
                page.Select(1);
                AssertSaved(page[0], 20, 4);
                page.Fill("5", "1");
                page.NothingMissing(1);
                Assert.False(page.NextIsStopped());
                AssertSaved(page[1], 5, 1);
            }
        }

        [Fact]
        public void NextWithoutMessageIsAsBefore()
        {
            using (Page page = new Page("arm_joint"))
            {
                page.Select(0);
                page.Fill("10", "2");
                Assert.False(page.NextIsStopped());
                AssertSaved(page[0], 10, 2);
                Assert.Null(page.Selected);
            }
        }
    }
}