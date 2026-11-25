using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Moq;
using SolidWorks.Interop.sldworks;
using SW2URDF.UI;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// An export of a fake assembly, without SolidWorks. It keeps the user preferences, the progress bar and
    /// which components are visible, and collects the messages instead of showing them.
    /// </summary>
    internal sealed class FakeExport : IDisposable
    {
        private readonly IMessageBox previousMessageBox = URDFPackage.MessageBox;
        private readonly Dictionary<string, Component2> components = new Dictionary<string, Component2>();
        private readonly HashSet<string> selected = new HashSet<string>();

        public readonly string Directory = Path.Combine(Path.GetTempPath(), "sw2urdf-export-" + Guid.NewGuid().ToString("N"));
        public readonly Dictionary<string, object> Preferences = new Dictionary<string, object>();
        public readonly HashSet<string> Visible = new HashSet<string>();
        public readonly List<string> Messages = new List<string>();
        public readonly Robot Robot = new Robot { Name = "package" };
        public readonly ExportHelper Helper;
        public readonly Mock<SldWorks> App = new Mock<SldWorks>();
        public readonly Mock<ModelDoc2> Document = new Mock<ModelDoc2>();
        public readonly Mock<ModelDocExtension> Extension = new Mock<ModelDocExtension>();

        public int ProgressStarts;
        public int ProgressEnds;

        // The steps of SolidWorks that a test can let fail.
        public Action OnSelectAll = () => { };
        public Action OnHide = () => { };
        public Action OnShow = () => { };

        /// <summary>Called when SolidWorks saves a mesh, with its path.</summary>
        public Func<string, bool> OnSaveAs;

        /// <param name="links">Names of the links, the first one is the base link and the others are its
        /// children. Each has a component of the same name.</param>
        public FakeExport(params string[] links)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Mock<IMessageBox> box = new Mock<IMessageBox>();
            box.Setup(b => b.Show(It.IsAny<string>())).Returns(MessageBoxResult.OK)
                .Callback((string message) => Messages.Add(message));
            URDFPackage.MessageBox = box.Object;
            // Nothing may open a window in a test.
            Assert.Equal(0, System.Windows.Forms.Application.OpenForms.Count);

            OnSaveAs = path =>
            {
                WriteStl(path);
                return true;
            };
            SetUpPreferences();
            SetUpDocument();

            UserProgressBar bar = ProgressBar();
            App.Setup(a => a.GetUserProgressBar(out bar)).Returns(true);
            App.Setup(a => a.GetCurrentLanguage()).Returns("english");
            App.SetupGet(a => a.ActiveDoc).Returns(Document.Object);

            Link baseLink = null;
            foreach (string name in links)
            {
                Link link = AddLink(name, baseLink);
                baseLink = baseLink ?? link;
            }
            Robot.SetBaseLink(baseLink);

            Helper = new ExportHelper(App.Object)
            {
                URDFRobot = Robot,
                PackageName = "package",
                SavePath = Directory
            };
        }

        public string UrdfFile
        {
            get { return Path.Combine(Directory, "package", "urdf", "package.urdf"); }
        }

        private Link AddLink(string name, Link baseLink)
        {
            Link link = baseLink == null ? new Link() : new Link(baseLink);
            link.Name = name;
            link.SWComponents = new List<Component2> { AddComponent(name) };
            link.SWMainComponent = link.SWComponents[0];
            link.Joint.CoordinateSystemName = "Origin_global";
            if (baseLink != null)
            {
                link.Joint.Name = "joint_" + name;
                link.Joint.Type = "fixed";
                link.Joint.Parent.Name = baseLink.Name;
                link.Joint.Child.Name = name;
                baseLink.Children.Add(link);
            }
            return link;
        }

        public Component2 AddComponent(string name)
        {
            Mock<Component2> component = new Mock<Component2>();
            component.SetupGet(c => c.Name2).Returns(name);
            component.Setup(c => c.IsHidden(It.IsAny<bool>())).Returns(() => !Visible.Contains(name));
            component.Setup(c => c.Select4(It.IsAny<bool>(), It.IsAny<SelectData>(), It.IsAny<bool>()))
                .Returns(() => selected.Add(name));
            components[name] = component.Object;
            Visible.Add(name);
            return component.Object;
        }

        private void SetUpDocument()
        {
            Mock<SelectionMgr> selection = new Mock<SelectionMgr>();
            selection.Setup(s => s.CreateSelectData()).Returns(new Mock<SelectData>().Object);
            Mock<MathTransform> frame = new Mock<MathTransform>();
            frame.SetupGet(t => t.ArrayData).Returns(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3, 1, 0, 0, 0 });
            Extension.Setup(e => e.GetCoordinateSystemTransformByName(It.IsAny<string>())).Returns(frame.Object);
            Extension.Setup(e => e.SelectByID2("Origin_global", "COORDSYS", 0, 0, 0, false, 0,
                It.IsAny<Callout>(), 0)).Returns(true);
            Extension.Setup(e => e.SelectAll()).Callback(() =>
            {
                OnSelectAll();
                selected.UnionWith(components.Keys);
            });
            int errors = 0, warnings = 0;
            Extension.Setup(e => e.SaveAs(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<object>(),
                ref errors, ref warnings)).Returns((string path, int version, int options, object data, int e, int w) => OnSaveAs(path));

            Document.SetupGet(d => d.Extension).Returns(Extension.Object);
            Document.SetupGet(d => d.SelectionManager).Returns(selection.Object);
            Document.SetupGet(d => d.FeatureManager).Returns(new Mock<FeatureManager>().Object);
            Document.Setup(d => d.GetTitle()).Returns("robot");
            Document.Setup(d => d.ClearSelection2(It.IsAny<bool>())).Callback(() => selected.Clear());
            Document.Setup(d => d.HideComponent2()).Callback(() =>
            {
                OnHide();
                Visible.ExceptWith(selected);
            });
            Document.Setup(d => d.ShowComponent2()).Callback(() =>
            {
                OnShow();
                Visible.UnionWith(selected);
            });
            Document.As<AssemblyDoc>().Setup(a => a.GetComponents(It.IsAny<bool>()))
                .Returns(() => components.Values.Cast<object>().ToArray());
        }

        // Every preference has the value of the user first.
        private void SetUpPreferences()
        {
            App.Setup(a => a.GetUserPreferenceToggle(It.IsAny<int>())).Returns((int id) => (bool)Preference("toggle " + id, false));
            App.Setup(a => a.SetUserPreferenceToggle(It.IsAny<int>(), It.IsAny<bool>()))
                .Callback((int id, bool value) => Preferences["toggle " + id] = value);
            App.Setup(a => a.GetUserPreferenceIntegerValue(It.IsAny<int>())).Returns((int id) => (int)Preference("integer " + id, 7));
            App.Setup(a => a.SetUserPreferenceIntegerValue(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(true).Callback((int id, int value) => Preferences["integer " + id] = value);
            App.Setup(a => a.GetUserPreferenceDoubleValue(It.IsAny<int>())).Returns((int id) => (double)Preference("double " + id, 1.5));
            App.Setup(a => a.SetUserPreferenceDoubleValue(It.IsAny<int>(), It.IsAny<double>()))
                .Returns(true).Callback((int id, double value) => Preferences["double " + id] = value);
        }

        private object Preference(string name, object valueOfTheUser)
        {
            if (!Preferences.ContainsKey(name)) Preferences[name] = valueOfTheUser;
            return Preferences[name];
        }

        /// <summary>The preferences that do not have the value of the user.</summary>
        public string ChangedPreferences()
        {
            return string.Join(", ", Preferences.Where(p => !Equals(p.Value, p.Key.StartsWith("toggle") ? false :
                p.Key.StartsWith("integer") ? (object)7 : 1.5)).Select(p => p.Key + " = " + p.Value));
        }

        private UserProgressBar ProgressBar()
        {
            Mock<UserProgressBar> bar = new Mock<UserProgressBar>();
            bar.Setup(b => b.Start(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>())).Returns(true)
                .Callback(() => ProgressStarts++);
            bar.Setup(b => b.End()).Callback(() => ProgressEnds++);
            return bar.Object;
        }

        /// <summary>A binary STL file with one triangle.</summary>
        public static void WriteStl(string path)
        {
            byte[] data = new byte[84 + 50];
            data[80] = 1;
            File.WriteAllBytes(path, data);
        }

        public void Dispose()
        {
            URDFPackage.MessageBox = previousMessageBox;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }
}
