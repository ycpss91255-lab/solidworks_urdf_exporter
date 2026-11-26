using System;
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
    /// Does not need SolidWorks. A mimic that leads back to its own joint is reported when the joint page is
    /// left (issue #200). The joints are checked by the code of the form.
    /// </summary>
    public class TestMimicLoops
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const string Key = "{0}: its mimic leads back to itself";

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Members).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, Members).SetValue(target, value);
        }

        // What the form adds to the message for a chain of joints, each of them written as "name" or
        // "name>mimicked name". Every joint is the child of the one before. The form is made without its
        // constructor, which needs SolidWorks, and is never shown.
        private static string[] Problems(params string[] joints)
        {
            using (AssemblyExportForm form =
                (AssemblyExportForm)FormatterServices.GetUninitializedObject(typeof(AssemblyExportForm)))
            {
                typeof(Form).GetConstructor(Type.EmptyTypes).Invoke(form, null);
                SetField(form, "ui", new UiText("english"));
                typeof(AssemblyExportForm).GetMethod("InitializeComponent", Members).Invoke(form, null);
                ExportHelper exporter = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
                exporter.URDFRobot = new Robot();
                Link parent = new Link { Name = "base_link" };
                exporter.URDFRobot.SetBaseLink(parent);
                form.Exporter = exporter;

                TreeNodeCollection nodes = Field<TreeView>(form, "treeViewJointTree").Nodes;
                foreach (string[] parts in joints.Select(joint => joint.Split('>')))
                {
                    Link child = new Link(parent) { Name = parts[0] + "_link" };
                    child.Joint.Name = parts[0];
                    child.Joint.Type = "continuous";
                    child.Joint.Parent.Name = parent.Name;
                    child.Joint.Child.Name = child.Name;
                    child.Joint.Axis.SetXYZ(new double[] { 0, 0, 1 });
                    if (parts.Length > 1)
                    {
                        child.Joint.Mimic.Update(parts[1], "1", "0");
                    }
                    parent.Children.Add(child);
                    LinkNode node = new LinkNode { Link = child };
                    nodes.Add(node);
                    nodes = node.Nodes;
                    parent = child;
                }
                string text = (string)typeof(AssemblyExportForm)
                    .GetMethod("CheckJointsForErrors", Members, null, Type.EmptyTypes, null).Invoke(form, null);
                return text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static string[] Reports(params string[] names)
        {
            return names.Select(name => string.Format(Key, name)).ToArray();
        }

        [Fact]
        public void JointThatMimicsItselfIsReported()
        {
            Assert.Equal(Reports("b"), Problems("a", "b>b", "c"));
            Assert.Equal(Reports("a"), Problems("a>a"));
        }

        [Fact]
        public void JointsThatMimicEachOtherAreReported()
        {
            Assert.Equal(Reports("a", "b"), Problems("a>b", "b>a"));
            Assert.Equal(Reports("a", "c", "d"), Problems("a>c", "b", "c>d", "d>a"));
        }

        [Fact]
        public void EveryLoopIsReported()
        {
            Assert.Equal(Reports("a", "b", "c"), Problems("a>b", "b>a", "c>c", "d"));
        }

        [Fact]
        public void JointThatMimicsAJointOfALoopIsNotPartOfIt()
        {
            Assert.Equal(Reports("b", "c"), Problems("a>b", "b>c", "c>b"));
        }

        [Fact]
        public void MimicsWithoutALoopAreNotReported()
        {
            Assert.Empty(Problems("a", "b", "c"));
            Assert.Empty(Problems("a", "b>a", "c>b", "d>a"));
            Assert.Empty(Problems("a>c", "b>a", "c"));
            // Joint names are case sensitive in URDF.
            Assert.Empty(Problems("a", "A>a"));
        }

        [Fact]
        public void UnknownAndDoubleNamesEndTheSearch()
        {
            string loop = string.Format(Key, "");
            Assert.DoesNotContain(Problems("a>gone", "b>a"), problem => problem.EndsWith(loop));
            // Two joints named a: the search ends and says nothing about the other one.
            Assert.DoesNotContain(Problems("a", "a>b", "b>c", "c"), problem => problem.EndsWith(loop));
        }

        [Fact]
        public void ReportIsTranslated()
        {
            Assert.Equal(Key, new UiText("english").Get(Key));
            Assert.NotEqual(Key, new UiText("chinese").Get(Key));
        }
    }
}