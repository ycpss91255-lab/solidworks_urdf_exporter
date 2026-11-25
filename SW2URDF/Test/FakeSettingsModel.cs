using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDFExport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SW2URDF.Test
{
    /// <summary>
    /// A document with a feature tree, configurations and exporter attributes for tests without
    /// SolidWorks. Attribute values are kept per configuration and read for the active one, like
    /// SolidWorks does; they follow a configuration that is renamed.
    /// </summary>
    internal sealed class FakeSettingsModel
    {
        public sealed class Node
        {
            public string Type;
            public string Name;
            public bool CanBeSelected = true;
            public bool CanBeWritten = true;
            public readonly Dictionary<int, string> Data = new Dictionary<int, string>();
            public readonly Dictionary<int, double> Version = new Dictionary<int, double>();
            public Feature Feature;
        }

        private sealed class Config
        {
            public int Id;
            public string Name;
        }

        public const string FolderType = "FtrFolder";
        public const string EndTag = "___EndTag___";

        public readonly List<Node> Tree = new List<Node>();
        public readonly List<string> Shown = new List<string>();
        public readonly List<Node> Selection = new List<Node>();
        public int Walks;
        public int Deletions;
        public int FoldersInserted;
        /// <summary>Whether SolidWorks refuses the n-th folder insertion (from 1).</summary>
        public Func<int, bool> InsertFails = n => false;
        public bool InsertThrows;
        public string Path = @"C:\model\robot.SLDASM";
        public bool ReadOnly;
        public bool SaveResult = true;
        public int SaveErrors;
        public int Saves;
        public bool SaveThrows;

        private readonly List<Config> configurations = new List<Config>();
        private Config active;
        private readonly Mock<ModelDoc2> document = new Mock<ModelDoc2>();
        private readonly Mock<SldWorks> app = new Mock<SldWorks>();

        private delegate void SaveCallback(int options, ref int errors, ref int warnings);

        public FakeSettingsModel(params string[] names)
        {
            foreach (string name in names.Length == 0 ? new[] { "Default" } : names)
            {
                configurations.Add(new Config { Id = configurations.Count, Name = name });
            }
            active = configurations[0];

            document.Setup(d => d.GetConfigurationNames()).Returns(() => configurations.Select(c => c.Name).ToArray());
            var configurationManager = new Mock<ConfigurationManager>();
            configurationManager.SetupGet(m => m.ActiveConfiguration).Returns(() =>
            {
                var configuration = new Mock<Configuration>();
                configuration.SetupGet(c => c.Name).Returns(active.Name);
                return configuration.Object;
            });
            document.SetupGet(d => d.ConfigurationManager).Returns(configurationManager.Object);
            document.Setup(d => d.ShowConfiguration2(It.IsAny<string>())).Returns((string name) =>
            {
                Shown.Add(name);
                Config found = configurations.FirstOrDefault(c => c.Name == name);
                active = found ?? active;
                return found != null;
            });

            document.Setup(d => d.FirstFeature()).Returns(() =>
            {
                Walks++;
                return Tree.Count == 0 ? null : Tree[0].Feature;
            });
            document.Setup(d => d.ClearSelection2(It.IsAny<bool>())).Callback(() => Selection.Clear());

            var extension = new Mock<ModelDocExtension>();
            extension.Setup(e => e.DeleteSelection2(It.IsAny<int>())).Returns(() =>
            {
                Deletions++;
                foreach (Node node in Selection)
                {
                    Tree.Remove(node);
                    if (node.Type == FolderType)
                    {
                        Tree.RemoveAll(n => n.Type == FolderType && n.Name == node.Name + EndTag);
                    }
                }
                Selection.Clear();
                return true;
            });
            document.SetupGet(d => d.Extension).Returns(extension.Object);

            var featureManager = new Mock<FeatureManager>();
            featureManager.Setup(m => m.GetFeatures(It.IsAny<bool>())).Returns(() =>
            {
                Walks++;
                return Tree.Select(n => (object)n.Feature).ToArray();
            });
            featureManager.Setup(m => m.InsertFeatureTreeFolder2(It.IsAny<int>())).Returns(() => InsertFolder());
            document.SetupGet(d => d.FeatureManager).Returns(featureManager.Object);

            document.Setup(d => d.GetPathName()).Returns(() => Path);
            document.Setup(d => d.IsOpenedReadOnly()).Returns(() => ReadOnly);
            document.Setup(d => d.Save3(It.IsAny<int>(), ref It.Ref<int>.IsAny, ref It.Ref<int>.IsAny))
                .Callback(new SaveCallback((int options, ref int errors, ref int warnings) =>
                {
                    Saves++;
                    errors = SaveErrors;
                    if (SaveThrows) throw new InvalidOperationException("save");
                }))
                .Returns(() => SaveResult);

            app.Setup(a => a.DefineAttribute(It.IsAny<string>())).Returns((string name) => DefineAttribute());
        }

        public ModelDoc2 Document { get { return document.Object; } }
        public SldWorks App { get { return app.Object; } }

        public void Activate(string configuration)
        {
            active = configurations.Single(c => c.Name == configuration);
        }

        public void RenameConfiguration(string from, string to)
        {
            configurations.Single(c => c.Name == from).Name = to;
        }

        /// <summary>An attribute of the exporter whose data belongs to one configuration.</summary>
        public Node AddEntry(string name, string data, string configuration = null, double version = 1.4)
        {
            Node node = AddAttribute(name, out SolidWorks.Interop.sldworks.Attribute ignored);
            int id = (configuration == null ? active : configurations.Single(c => c.Name == configuration)).Id;
            node.Data[id] = data;
            node.Version[id] = version;
            return node;
        }

        /// <summary>An attribute whose data is the same in all configurations.</summary>
        public Node AddShared(string name, string data, double version = 1.4)
        {
            Node node = AddAttribute(name, out SolidWorks.Interop.sldworks.Attribute ignored);
            foreach (Config configuration in configurations)
            {
                node.Data[configuration.Id] = data;
                node.Version[configuration.Id] = version;
            }
            return node;
        }

        public Node Add(string type, string name)
        {
            Node node = NewNode(type, name);
            Tree.Add(node);
            return node;
        }

        /// <summary>Puts a folder around the named nodes.</summary>
        public void AddFolder(string name, params string[] content)
        {
            WrapInFolder(Tree.Where(n => content.Contains(n.Name)).ToList(), name);
        }

        public Node Find(string name)
        {
            return Tree.FirstOrDefault(n => n.Name == name);
        }

        /// <summary>The data of the named entry for the active configuration.</summary>
        public string DataOf(string name)
        {
            Node node = Find(name);
            string data = null;
            return node != null && node.Data.TryGetValue(active.Id, out data) ? data : null;
        }

        public string[] ContentOf(string folder)
        {
            int start = Tree.FindIndex(n => n.Type == FolderType && n.Name == folder);
            return start < 0 ? null :
                Tree.Skip(start + 1).TakeWhile(n => n.Name != folder + EndTag).Select(n => n.Name).ToArray();
        }

        private Node NewNode(string type, string name)
        {
            Node node = new Node { Type = type, Name = name };
            var feature = new Mock<Feature>();
            feature.Setup(f => f.GetTypeName2()).Returns(() => node.Type);
            feature.SetupGet(f => f.Name).Returns(() => node.Name);
            feature.SetupSet(f => f.Name = It.IsAny<string>()).Callback((string value) =>
            {
                if (Tree.Any(n => n.Name == value)) return;
                foreach (Node end in Tree.Where(n => node.Type == FolderType && n.Name == node.Name + EndTag))
                {
                    end.Name = value + EndTag;
                }
                node.Name = value;
            });
            feature.Setup(f => f.GetNextFeature()).Returns(() =>
            {
                int next = Tree.IndexOf(node) + 1;
                return next > 0 && next < Tree.Count ? Tree[next].Feature : null;
            });
            feature.Setup(f => f.Select2(It.IsAny<bool>(), It.IsAny<int>())).Returns((bool append, int mark) =>
            {
                if (!append) Selection.Clear();
                if (!node.CanBeSelected || !Tree.Contains(node)) return false;
                Selection.Add(node);
                return true;
            });
            node.Feature = feature.Object;
            return node;
        }

        private Node AddAttribute(string name, out SolidWorks.Interop.sldworks.Attribute result)
        {
            Node node = NewNode("Attribute", name);
            var attribute = new Mock<SolidWorks.Interop.sldworks.Attribute>();
            attribute.Setup(a => a.GetName()).Returns(() => node.Name);

            var data = new Mock<Parameter>();
            data.Setup(p => p.GetStringValue()).Returns(() =>
                node.Data.TryGetValue(active.Id, out string value) ? value : "");
            data.Setup(p => p.SetStringValue2(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
                .Returns((string value, int options, string configuration) =>
                {
                    if (!node.CanBeWritten) return false;
                    node.Data[active.Id] = value;
                    return true;
                });
            var version = new Mock<Parameter>();
            version.Setup(p => p.GetDoubleValue()).Returns(() =>
                node.Version.TryGetValue(active.Id, out double value) ? value : 0.0);
            version.Setup(p => p.SetDoubleValue2(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<string>()))
                .Returns((double value, int options, string configuration) =>
                {
                    if (!node.CanBeWritten) return false;
                    node.Version[active.Id] = value;
                    return true;
                });
            attribute.Setup(a => a.GetParameter(It.IsAny<string>())).Returns(new Mock<Parameter>().Object);
            attribute.Setup(a => a.GetParameter("data")).Returns(data.Object);
            attribute.Setup(a => a.GetParameter("exporterVersion")).Returns(version.Object);

            result = attribute.Object;
            Mock.Get(node.Feature).Setup(f => f.GetSpecificFeature2()).Returns(attribute.Object);
            Tree.Add(node);
            return node;
        }

        private AttributeDef DefineAttribute()
        {
            var definition = new Mock<AttributeDef>();
            definition.Setup(d => d.CreateInstance5(It.IsAny<ModelDoc2>(), It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>())).Returns((ModelDoc2 model, object component, string name, int options, int which) =>
                {
                    AddAttribute(name, out SolidWorks.Interop.sldworks.Attribute created);
                    return created;
                });
            return definition.Object;
        }

        private Feature InsertFolder()
        {
            FoldersInserted++;
            if (InsertThrows) throw new InvalidOperationException("folder");
            if (InsertFails(FoldersInserted) || Selection.Count == 0) return null;
            return WrapInFolder(Selection.ToList(), "Folder" + FoldersInserted).Feature;
        }

        private Node WrapInFolder(List<Node> nodes, string name)
        {
            Node folder = NewNode(FolderType, name);
            Tree.RemoveAll(nodes.Contains);
            Tree.Add(folder);
            Tree.AddRange(nodes);
            Tree.Add(NewNode(FolderType, name + EndTag));
            return folder;
        }
    }

    /// <summary>
    /// Answers the message boxes of the exporter instead of showing them, for the time of a test.
    /// </summary>
    internal sealed class FakeMessageBox : IMessageBox, IDisposable
    {
        private readonly IMessageBox previous = URDFPackage.MessageBox;
        public readonly List<string> Messages = new List<string>();
        /// <summary>The answers to yes/no questions in their order, No when there are no more.</summary>
        public readonly Queue<MessageBoxResult> Answers = new Queue<MessageBoxResult>();

        public FakeMessageBox()
        {
            URDFPackage.MessageBox = this;
        }

        public MessageBoxResult Show(string message)
        {
            Messages.Add(message);
            return MessageBoxResult.OK;
        }

        public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons)
        {
            Messages.Add(message);
            if (buttons == MessageBoxButton.OK) return MessageBoxResult.OK;
            return Answers.Count > 0 ? Answers.Dequeue() : MessageBoxResult.No;
        }

        public void Dispose()
        {
            URDFPackage.MessageBox = previous;
        }
    }
}
