using SW2URDF.URDF;
using SW2URDF.URDFExport;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>The check of the joint names before an export. Does not need SolidWorks.</summary>
    public class TestJointNamesUnique
    {
        private static LinkNode Node(string link, string joint, params LinkNode[] children)
        {
            LinkNode node = new LinkNode(new Link { Name = link });
            node.Link.Joint.Name = joint;
            node.Nodes.AddRange(children);
            return node;
        }

        private static List<string[]> JointConflicts(LinkNode root)
        {
            var manager = (ExportPropertyManager)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ExportPropertyManager));
            var conflicts = new List<List<string>>();
            manager.CheckIfJointNamesAreUnique(root, root, conflicts);
            return conflicts.Where(c => c.Count > 1).Select(c => c.ToArray()).ToList();
        }

        [Fact]
        public void TwoJointsWithTheSameNameAreFound()
        {
            LinkNode root = Node("base", "", Node("a", "joint"), Node("b", "joint"));
            Assert.Equal(new[] { "joint", "joint" }, Assert.Single(JointConflicts(root)));
        }

        [Fact]
        public void TheSameNameFurtherDownTheTreeIsFound()
        {
            LinkNode root = Node("base", "", Node("a", "joint", Node("b", "other", Node("c", "joint"))));
            Assert.Equal(new[] { "joint", "joint" }, Assert.Single(JointConflicts(root)));
        }

        [Fact]
        public void ALinkWithTheNameOfAJointIsNoConflict()
        {
            LinkNode root = Node("base", "", Node("a", "b"), Node("b", "joint"), Node("c", "c"));
            Assert.Empty(JointConflicts(root));
        }

        [Fact]
        public void DifferentNamesAreNoConflict()
        {
            LinkNode root = Node("base", "", Node("a", "joint_a", Node("b", "joint_b")));
            Assert.Empty(JointConflicts(root));
        }

        [Fact]
        public void JointsWithoutANameAreNoConflict()
        {
            // The check for incomplete links reports them
            Assert.Empty(JointConflicts(Node("base", "", Node("a", ""), Node("b", ""))));
        }

        [Fact]
        public void TheBaseLinkHasNoJoint()
        {
            // A name left there is not written to the robot
            LinkNode root = Node("base", "joint", Node("a", "joint"));
            Assert.Empty(JointConflicts(root));
        }
    }
}
