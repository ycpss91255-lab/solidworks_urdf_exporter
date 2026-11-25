using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// What is typed on the link page is part of the settings that an export stores. Does not need
    /// SolidWorks: the form is never shown, and the export stops where the settings are stored.
    /// </summary>
    public class TestLinkPageSettings
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class StopHere : Exception
        {
        }

        private class SilentForm : AssemblyExportForm
        {
            // Never called, the form is made without a constructor.
            private SilentForm() : base(null, null, null)
            {
            }

            // The masses when the configuration was to be saved, the base link first.
            public double[] SavedMasses;

            public override void SaveConfigTree(ModelDoc2 model, LinkNode baseNode, bool warnUser)
            {
                SavedMasses = new[] { baseNode }.Concat(baseNode.Nodes.Cast<LinkNode>())
                    .Select(node => node.Link.Inertial.Mass.Value).ToArray();
                throw new StopHere();
            }
        }

        private static T Field<T>(object form, string name)
        {
            return (T)typeof(AssemblyExportForm).GetField(name, Members).GetValue(form);
        }

        private static void SetField(object form, string name, object value)
        {
            typeof(AssemblyExportForm).GetField(name, Members).SetValue(form, value);
        }

        /// <summary>Types a mass for the selected link and finishes the export.</summary>
        private static double[] Finish(int selected, string mass, string handler)
        {
            double[] saved = null;
            SilentFormThread.Run(() => saved = FinishOnThisThread(selected, mass, handler));
            return saved;
        }

        private static double[] FinishOnThisThread(int selected, string mass, string handler)
        {
            SilentForm form = (SilentForm)FormatterServices.GetUninitializedObject(typeof(SilentForm));
            typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
            using (form)
            {
                SetField(form, "ui", new UiText("english"));
                typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
                SetField(form, "linkBoxes", new[] { "textBoxMass", "comboBoxMaterials", "textBoxTexture" }
                    .Select(name => Field<Control>(form, name)).ToArray());
                SetField(form, "jointBoxes", new Control[0]);
                var selection = new Mock<SelectionMgr>();
                selection.Setup(m => m.CreateSelectData()).Returns(new Mock<SelectData>().Object);
                var model = new Mock<ModelDoc2>();
                model.Setup(d => d.SelectionManager).Returns(selection.Object);
                SetField(form, "ActiveSWModel", model.Object);

                Link baseLink = new Link { Name = "base_link", SWComponents = new List<Component2>() };
                baseLink.Inertial.Mass.Value = 1;
                Link arm = new Link(baseLink) { Name = "arm_link", SWComponents = new List<Component2>() };
                arm.Inertial.Mass.Value = 2;
                LinkNode baseNode = new LinkNode { Link = baseLink, IsBaseNode = true, Text = baseLink.Name };
                LinkNode armNode = new LinkNode { Link = arm, Text = arm.Name };
                baseNode.Nodes.Add(armNode);
                SetField(form, "BaseNode", baseNode);

                TreeView tree = Field<TreeView>(form, "treeViewLinkProperties");
                Assert.NotEqual(IntPtr.Zero, tree.Handle);
                form.FillLinkTree();
                tree.SelectedNode = selected == 0 ? baseNode : armNode;
                TextBox box = Field<TextBox>(form, "textBoxMass");
                Assert.Equal((selected + 1).ToString(), box.Text);
                box.Text = mass;

                var thrown = Assert.Throws<TargetInvocationException>(() =>
                    typeof(AssemblyExportForm).GetMethod(handler, Members).Invoke(form, new object[] { form, EventArgs.Empty }));
                Assert.IsType<StopHere>(thrown.InnerException);
                return form.SavedMasses;
            }
        }

        [Fact]
        public void TheLinkThatIsShownIsStoredWithTheExport()
        {
            Assert.Equal(new[] { 1.0, 7.5 }, Finish(1, "7.5", "ButtonLinksFinishClick"));
        }

        [Fact]
        public void TheBaseLinkIsStoredWithTheExport()
        {
            Assert.Equal(new[] { 7.5, 2.0 }, Finish(0, "7.5", "ButtonLinksFinishClick"));
        }

        [Fact]
        public void TheSameHoldsForTheExportOfTheModelFileOnly()
        {
            Assert.Equal(new[] { 1.0, 7.5 }, Finish(1, "7.5", "ButtonLinksExportUrdfOnlyClick"));
        }
    }
}
