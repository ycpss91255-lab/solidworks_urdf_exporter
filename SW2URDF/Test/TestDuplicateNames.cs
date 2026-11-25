using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. Two joints of a robot must not have the same name; the property
    /// manager checks this before it goes on to the export form.
    /// </summary>
    public class TestDuplicateNames
    {
        private static LinkNode Node(string link, string joint, params LinkNode[] children)
        {
            LinkNode node = new LinkNode();
            node.Link.Name = link;
            node.Link.Joint.Name = joint;
            foreach (LinkNode child in children)
            {
                node.Nodes.Add(child);
            }
            return node;
        }

        private static string[] JointConflicts(LinkNode root)
        {
            ExportPropertyManager manager =
                (ExportPropertyManager)FormatterServices.GetUninitializedObject(typeof(ExportPropertyManager));
            List<List<string>> conflicts = new List<List<string>>();
            manager.CheckIfJointNamesAreUnique(root, root, conflicts);
            return conflicts.Where(conflict => conflict.Count > 1)
                .Select(conflict => string.Join(",", conflict)).ToArray();
        }

        [Fact]
        public void DifferentNamesAreNoConflict()
        {
            Assert.Empty(JointConflicts(Node("base_link", "", Node("a", "joint_a", Node("b", "joint_b")))));
        }

        [Fact]
        public void TwoBranchesWithTheSameJointName()
        {
            Assert.Equal(new[] { "joint,joint" },
                JointConflicts(Node("base_link", "", Node("a", "joint"), Node("b", "joint"))));
        }

        [Fact]
        public void AJointAndOneFurtherDown()
        {
            Assert.Equal(new[] { "joint,joint" },
                JointConflicts(Node("base_link", "", Node("a", "joint", Node("b", "other", Node("c", "joint"))))));
        }

        [Fact]
        public void EveryNameIsReportedOnce()
        {
            Assert.Equal(new[] { "one,one", "two,two,two" }, JointConflicts(Node("base_link", "",
                Node("a", "one", Node("b", "two")), Node("c", "two", Node("d", "one")), Node("e", "two"))));
        }

        [Fact]
        public void ALinkMayHaveTheNameOfAJoint()
        {
            Assert.Empty(JointConflicts(Node("base_link", "wheel", Node("a", "axle", Node("wheel", "wheel")))));
        }

        // Reported by the check for incomplete links, which says which link it is
        [Fact]
        public void BlankNamesAreNoConflict()
        {
            Assert.Empty(JointConflicts(Node("base_link", "", Node("a", ""), Node("b", ""), Node("c", null))));
        }

        // The base link has no joint; a name left on it is not exported
        [Fact]
        public void TheBaseLinkHasNoJoint()
        {
            Assert.Empty(JointConflicts(Node("base_link", "joint", Node("a", "joint"))));
        }
    }
}
