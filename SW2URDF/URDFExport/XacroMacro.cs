using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using SW2URDF.URDF;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// The xacro output as a robot macro (issue 139). Two files are written into the urdf folder:
    /// <c>&lt;robot&gt;_macro.xacro</c> defines the macro and <c>&lt;robot&gt;.xacro</c> includes it and calls it
    /// with its defaults, so expanding <c>&lt;robot&gt;.xacro</c> gives the plain URDF and the launch files keep
    /// working. Another description includes the macro file and calls the macro with
    /// <c>prefix</c> (put in front of every link and joint name), <c>parent</c> (a link the base link is attached
    /// to with a fixed joint; empty: no joint) and <c>xyz</c> / <c>rpy</c> (the origin of that joint).
    /// </summary>
    public static class XacroMacro
    {
        public const string MacroFileSuffix = "_macro";
        public const string Parameters = "prefix:='' parent:='' xyz:='0 0 0' rpy:='0 0 0'";
        public const string ParentJointSuffix = "_parent_joint";
        public const string Prefix = "${prefix}";

        private static readonly string[] XacroKeywords =
        {
            "macro", "include", "property", "if", "unless", "insert_block", "arg", "element", "attribute", "call",
            "eval-comments", "loop"
        };

        /// <summary>"demo.xacro" gives "demo_macro.xacro".</summary>
        public static string MacroFileName(string robotFileName)
        {
            return Path.GetFileNameWithoutExtension(robotFileName) + MacroFileSuffix + XacroOutput.XacroExtension;
        }

        /// <summary>
        /// The macro name: the robot name with every character other than a letter, digit or underscore replaced
        /// by an underscore, an underscore in front when it does not start with a letter or underscore, and
        /// "_robot" after it when it is a xacro keyword.
        /// </summary>
        public static string MacroName(string robotName)
        {
            StringBuilder name = new StringBuilder();
            foreach (char c in robotName ?? "")
            {
                name.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '_') ? c : '_');
            }
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
            {
                name.Insert(0, '_');
            }
            string result = name.ToString();
            return XacroKeywords.Contains(result) ? result + "_robot" : result;
        }

        /// <summary>Writes the plain URDF, or with xacro the macro file and the robot file that calls it.</summary>
        public static void WriteRobotFile(Robot robot, string robotFilePath, bool xacro)
        {
            if (xacro)
            {
                Write(robot, robotFilePath);
                return;
            }
            URDFWriter writer = new URDFWriter(robotFilePath);
            robot.WriteURDF(writer.writer);
        }

        /// <summary>Writes the macro file and the robot file that calls it.</summary>
        public static void Write(Robot robot, string robotFilePath)
        {
            XmlDocument plain = PlainDocument(robot);
            string macroFileName = MacroFileName(Path.GetFileName(robotFilePath));
            string macroName = MacroName(robot.Name);
            string directory = Path.GetDirectoryName(robotFilePath) ?? "";
            Save(BuildMacro(plain, macroName, robot.BaseLink.Name), Path.Combine(directory, macroFileName));
            Save(BuildRobotFile(plain, macroFileName, macroName), robotFilePath);
        }

        /// <summary>The robot as the plain URDF writer writes it.</summary>
        public static XmlDocument PlainDocument(Robot robot)
        {
            bool xacro = robot.WriteXacro;
            robot.WriteXacro = false;
            StringBuilder text = new StringBuilder();
            try
            {
                robot.WriteURDF(XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }));
            }
            finally
            {
                robot.WriteXacro = xacro;
            }
            XmlDocument document = new XmlDocument { XmlResolver = null };
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader(text.ToString()), settings))
            {
                document.Load(reader);
            }
            return document;
        }

        /// <summary>
        /// The macro file: the children of the plain robot inside <c>xacro:macro</c>, with "$" written as "$$"
        /// (xacro reads "$" as the start of an expression), <c>${prefix}</c> in front of every link and joint
        /// name and link reference, and the fixed joint to <c>parent</c> inside <c>xacro:if</c>.
        /// </summary>
        public static XmlDocument BuildMacro(XmlDocument plain, string macroName, string baseLinkName)
        {
            XmlDocument document = NewDocument(plain);
            XmlElement robot = document.CreateElement("robot");
            robot.SetAttribute("xmlns:" + XacroOutput.NamespacePrefix, XacroOutput.NamespaceUri);
            document.AppendChild(robot);

            XmlElement macro = XacroElement(document, "macro");
            macro.SetAttribute("name", macroName);
            macro.SetAttribute("params", Parameters);
            robot.AppendChild(macro);

            foreach (XmlNode node in plain.DocumentElement.ChildNodes)
            {
                XmlNode copy = document.ImportNode(node, true);
                EscapeDollars(copy);
                AddPrefix(copy);
                macro.AppendChild(copy);
            }

            XmlElement condition = XacroElement(document, "if");
            condition.SetAttribute("value", "${parent != ''}");
            XmlElement joint = document.CreateElement("joint");
            joint.SetAttribute("name", Prefix + Escape(baseLinkName) + ParentJointSuffix);
            joint.SetAttribute("type", "fixed");
            XmlElement origin = document.CreateElement("origin");
            origin.SetAttribute("xyz", "${xyz}");
            origin.SetAttribute("rpy", "${rpy}");
            XmlElement parent = document.CreateElement("parent");
            parent.SetAttribute("link", "${parent}");
            XmlElement child = document.CreateElement("child");
            child.SetAttribute("link", Prefix + Escape(baseLinkName));
            joint.AppendChild(origin);
            joint.AppendChild(parent);
            joint.AppendChild(child);
            condition.AppendChild(joint);
            macro.AppendChild(condition);
            return document;
        }

        /// <summary>The robot file: includes the macro file (next to it) and calls the macro with its defaults.</summary>
        public static XmlDocument BuildRobotFile(XmlDocument plain, string macroFileName, string macroName)
        {
            XmlDocument document = NewDocument(plain);
            XmlElement robot = document.CreateElement("robot");
            robot.SetAttribute("name", plain.DocumentElement.GetAttribute("name"));
            robot.SetAttribute("xmlns:" + XacroOutput.NamespacePrefix, XacroOutput.NamespaceUri);
            document.AppendChild(robot);
            XmlElement include = XacroElement(document, "include");
            include.SetAttribute("filename", macroFileName);
            robot.AppendChild(include);
            robot.AppendChild(XacroElement(document, macroName));
            return document;
        }

        private static XmlDocument NewDocument(XmlDocument plain)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null };
            document.AppendChild(document.CreateXmlDeclaration("1.0", "utf-8", null));
            foreach (XmlNode node in plain.ChildNodes)
            {
                if (node is XmlComment)
                {
                    document.AppendChild(document.ImportNode(node, true));
                }
            }
            return document;
        }

        private static XmlElement XacroElement(XmlDocument document, string localName)
        {
            return document.CreateElement(XacroOutput.NamespacePrefix, localName, XacroOutput.NamespaceUri);
        }

        private static string Escape(string text)
        {
            return (text ?? "").Replace("$", "$$");
        }

        private static void EscapeDollars(XmlNode node)
        {
            if (node.Attributes != null)
            {
                foreach (XmlAttribute attribute in node.Attributes)
                {
                    attribute.Value = Escape(attribute.Value);
                }
            }
            if (node is XmlText || node is XmlCDataSection)
            {
                node.Value = Escape(node.Value);
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                EscapeDollars(child);
            }
        }

        private static void AddPrefix(XmlNode node)
        {
            if (!(node is XmlElement element))
            {
                return;
            }
            if (element.Name == "link")
            {
                PrefixAttribute(element, "name");
            }
            else if (element.Name == "joint")
            {
                PrefixAttribute(element, "name");
                foreach (XmlElement part in element.ChildNodes.OfType<XmlElement>())
                {
                    if (part.Name == "parent" || part.Name == "child")
                    {
                        PrefixAttribute(part, "link");
                    }
                    else if (part.Name == "mimic")
                    {
                        PrefixAttribute(part, "joint");
                    }
                }
            }
        }

        private static void PrefixAttribute(XmlElement element, string attribute)
        {
            if (element.HasAttribute(attribute))
            {
                element.SetAttribute(attribute, Prefix + element.GetAttribute(attribute));
            }
        }

        private static void Save(XmlDocument document, string path)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineOnAttributes = true,
            };
            using (XmlWriter writer = XmlWriter.Create(path, settings))
            {
                document.Save(writer);
            }
        }
    }
}
