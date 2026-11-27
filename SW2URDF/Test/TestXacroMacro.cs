using System;
using System.IO;
using System.Linq;
using System.Xml;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// The xacro output as a robot macro (issue 139). Does not need SolidWorks, so it has no fixture.
    /// </summary>
    public class TestXacroMacro
    {
        private static XmlNamespaceManager Namespaces(XmlDocument document)
        {
            XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("xacro", XacroOutput.NamespaceUri);
            return namespaces;
        }

        private static Robot BuildRobot()
        {
            Robot robot = new Robot { Name = "demo" };
            robot.BaseLink.Name = "base_link";
            robot.BaseLink.Visual.Geometry.Mesh.Filename = "package://demo/meshes/base_link.STL";
            Link arm = new Link(robot.BaseLink) { Name = "arm_link" };
            arm.Joint.Name = "arm_joint";
            arm.Joint.Type = "revolute";
            arm.Joint.Parent.Name = "base_link";
            arm.Joint.Child.Name = "arm_link";
            arm.Joint.Limit.Lower = -1;
            arm.Joint.Limit.Upper = 1;
            arm.Joint.Limit.Effort = 2;
            arm.Joint.Limit.Velocity = 3;
            arm.Inertial.Mass.Value = 1.5;
            arm.Visual.Geometry.Mesh.Filename = "package://demo/meshes/arm_link.STL";
            arm.Collision.Geometry.Mesh.Filename = "package://demo/meshes/arm_link.STL";
            robot.BaseLink.Children.Add(arm);
            return robot;
        }

        private static XmlElement Macro(XmlDocument document)
        {
            return (XmlElement)document.SelectSingleNode("/robot/xacro:macro", Namespaces(document));
        }

        // What xacro does with the default parameters: ${prefix} is empty, the xacro:if on an empty parent
        // is dropped and "$$" is a literal "$".
        private static string ExpandWithDefaults(XmlDocument macroFile)
        {
            XmlElement macro = (XmlElement)Macro(macroFile).CloneNode(true);
            foreach (XmlNode condition in macro.SelectNodes("xacro:if", Namespaces(macroFile)).Cast<XmlNode>().ToList())
            {
                macro.RemoveChild(condition);
            }
            return macro.InnerXml.Replace(XacroMacro.Prefix, "").Replace("$$", "$");
        }

        [Fact]
        public void FileAndMacroNames()
        {
            Assert.Equal("demo_macro.xacro", XacroMacro.MacroFileName("demo.xacro"));
            Assert.Equal("demo", XacroMacro.MacroName("demo"));
            Assert.Equal("_3_DOF_ARM_description", XacroMacro.MacroName("3_DOF_ARM_description"));
            Assert.Equal("my_robot_v2", XacroMacro.MacroName("my-robot.v2"));
            Assert.Equal("include_robot", XacroMacro.MacroName("include"));
            Assert.Equal("_", XacroMacro.MacroName(""));
            Assert.Equal("arm__", XacroMacro.MacroName("arm機器"));
        }

        [Fact]
        public void TheMacroTakesPrefixParentAndPose()
        {
            XmlDocument macroFile = XacroMacro.BuildMacro(XacroMacro.PlainDocument(BuildRobot()), "demo", "base_link");
            XmlElement macro = Macro(macroFile);
            Assert.NotNull(macro);
            Assert.Equal("demo", macro.GetAttribute("name"));
            Assert.Equal("prefix:='' parent:='' xyz:='0 0 0' rpy:='0 0 0'", macro.GetAttribute("params"));
            Assert.Equal(XacroOutput.NamespaceUri, macroFile.DocumentElement.GetAttribute("xmlns:xacro"));
            Assert.False(macroFile.DocumentElement.HasAttribute("name"));
        }

        [Fact]
        public void LinkAndJointNamesGetThePrefixAndMeshPathsDoNot()
        {
            XmlDocument macroFile = XacroMacro.BuildMacro(XacroMacro.PlainDocument(BuildRobot()), "demo", "base_link");
            XmlElement macro = Macro(macroFile);

            Assert.Equal(new[] { "${prefix}base_link", "${prefix}arm_link" },
                macro.SelectNodes("link/@name").Cast<XmlNode>().Select(a => a.Value).ToArray());
            XmlElement joint = (XmlElement)macro.SelectSingleNode("joint");
            Assert.Equal("${prefix}arm_joint", joint.GetAttribute("name"));
            Assert.Equal("${prefix}base_link", ((XmlElement)joint.SelectSingleNode("parent")).GetAttribute("link"));
            Assert.Equal("${prefix}arm_link", ((XmlElement)joint.SelectSingleNode("child")).GetAttribute("link"));
            Assert.Equal(new[] { "package://demo/meshes/base_link.STL", "package://demo/meshes/arm_link.STL",
                "package://demo/meshes/arm_link.STL" },
                macro.SelectNodes("link//mesh/@filename").Cast<XmlNode>().Select(a => a.Value).ToArray());
        }

        [Fact]
        public void AParentAttachesTheBaseLinkWithAFixedJoint()
        {
            XmlDocument macroFile = XacroMacro.BuildMacro(XacroMacro.PlainDocument(BuildRobot()), "demo", "base_link");
            XmlElement condition = (XmlElement)Macro(macroFile).SelectSingleNode("xacro:if", Namespaces(macroFile));
            Assert.NotNull(condition);
            Assert.Equal("${parent != ''}", condition.GetAttribute("value"));

            XmlElement joint = (XmlElement)condition.SelectSingleNode("joint");
            Assert.Equal("${prefix}base_link_parent_joint", joint.GetAttribute("name"));
            Assert.Equal("fixed", joint.GetAttribute("type"));
            Assert.Equal("${xyz}", ((XmlElement)joint.SelectSingleNode("origin")).GetAttribute("xyz"));
            Assert.Equal("${rpy}", ((XmlElement)joint.SelectSingleNode("origin")).GetAttribute("rpy"));
            Assert.Equal("${parent}", ((XmlElement)joint.SelectSingleNode("parent")).GetAttribute("link"));
            Assert.Equal("${prefix}base_link", ((XmlElement)joint.SelectSingleNode("child")).GetAttribute("link"));
        }

        [Fact]
        public void TheDefaultsGiveThePlainRobot()
        {
            XmlDocument plain = XacroMacro.PlainDocument(BuildRobot());
            XmlDocument macroFile = XacroMacro.BuildMacro(plain, "demo", "base_link");
            Assert.Equal(plain.DocumentElement.InnerXml, ExpandWithDefaults(macroFile));
        }

        [Fact]
        public void DollarSignsAreWrittenSoXacroKeepsThem()
        {
            Robot robot = BuildRobot();
            robot.BaseLink.Children[0].Visual.Geometry.Mesh.Filename = "package://demo/meshes/a$b.STL";
            XmlDocument plain = XacroMacro.PlainDocument(robot);
            XmlDocument macroFile = XacroMacro.BuildMacro(plain, "demo", "base_link");

            Assert.Contains("package://demo/meshes/a$$b.STL", Macro(macroFile).InnerXml);
            Assert.Equal(plain.DocumentElement.InnerXml, ExpandWithDefaults(macroFile));
        }

        [Fact]
        public void TheRobotFileIncludesTheMacroAndCallsItWithItsDefaults()
        {
            XmlDocument plain = XacroMacro.PlainDocument(BuildRobot());
            XmlDocument robotFile = XacroMacro.BuildRobotFile(plain, "demo_macro.xacro", "demo");
            XmlNamespaceManager namespaces = Namespaces(robotFile);

            Assert.Equal("demo", robotFile.DocumentElement.GetAttribute("name"));
            XmlElement[] children = robotFile.DocumentElement.ChildNodes.OfType<XmlElement>().ToArray();
            Assert.Equal(2, children.Length);
            Assert.Equal("include", children[0].LocalName);
            Assert.Equal(XacroOutput.NamespaceUri, children[0].NamespaceURI);
            Assert.Equal("demo_macro.xacro", children[0].GetAttribute("filename"));
            Assert.Equal("demo", children[1].LocalName);
            Assert.Equal(XacroOutput.NamespaceUri, children[1].NamespaceURI);
            Assert.Equal(0, children[1].Attributes.Count);
            Assert.Null(robotFile.SelectSingleNode("//link", namespaces));
        }

        [Fact]
        public void WritingCreatesBothFilesAndKeepsTheRobotChoice()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xacro_macro_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Robot robot = BuildRobot();
                robot.WriteXacro = true;
                string robotFile = Path.Combine(dir, "demo.xacro");
                XacroMacro.WriteRobotFile(robot, robotFile, true);
                Assert.True(robot.WriteXacro);

                Assert.Equal(new[] { "demo.xacro", "demo_macro.xacro" },
                    Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                string macroText = File.ReadAllText(Path.Combine(dir, "demo_macro.xacro"));
                Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", macroText);
                Assert.Contains("This URDF was automatically created", macroText);
                Assert.Contains("<xacro:macro", macroText);
                Assert.Contains("<xacro:include", File.ReadAllText(robotFile));
                Assert.False(File.ReadAllBytes(robotFile).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void WithoutXacroOnlyThePlainUrdfIsWritten()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xacro_macro_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string robotFile = Path.Combine(dir, "demo.urdf");
                XacroMacro.WriteRobotFile(BuildRobot(), robotFile, false);
                Assert.Equal(new[] { "demo.urdf" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
                string text = File.ReadAllText(robotFile);
                Assert.DoesNotContain("xacro", text);
                Assert.DoesNotContain("${prefix}", text);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
