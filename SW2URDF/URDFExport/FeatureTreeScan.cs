using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// One walk of the feature tree: type and name of every feature, and for attributes their name
    /// and whether they are entries of the exporter (they have a "data" and an "exporterVersion"
    /// parameter). Every COM object the walk received is released when the scan is disposed, so
    /// nothing from it may be used afterwards.
    /// </summary>
    internal sealed class FeatureTreeScan : IDisposable
    {
        /// <summary>Releases one reference of a received object. Replaceable for tests.</summary>
        internal static Action<object> Release = ReleaseComObject;

        internal sealed class Item
        {
            public Feature Feature;
            public string Type;
            public string Name;
            /// <summary>Only for features of type "Attribute", null for the others.</summary>
            public SolidWorks.Interop.sldworks.Attribute Attribute;
            public string AttributeName;
            public bool IsEntry;
        }

        private readonly List<Item> items = new List<Item>();
        private readonly List<object> received = new List<object>();
        private bool disposed;

        public FeatureTreeScan(ModelDoc2 model)
        {
            try
            {
                Feature feature = model.FirstFeature() as Feature;
                while (feature != null)
                {
                    Keep(feature);
                    Item item = new Item { Feature = feature, Type = feature.GetTypeName2(), Name = feature.Name };
                    if (item.Type == "Attribute")
                    {
                        item.Attribute = Keep(feature.GetSpecificFeature2()) as SolidWorks.Interop.sldworks.Attribute;
                        if (item.Attribute != null)
                        {
                            item.AttributeName = item.Attribute.GetName();
                            item.IsEntry = Keep(item.Attribute.GetParameter("data")) != null &&
                                Keep(item.Attribute.GetParameter("exporterVersion")) != null;
                        }
                    }
                    items.Add(item);
                    feature = feature.GetNextFeature() as Feature;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IReadOnlyList<Item> Items { get { return items; } }

        /// <summary>The attributes of the exporter, the shared ones included.</summary>
        public IEnumerable<Item> Entries { get { return items.Where(item => item.IsEntry); } }

        public string[] Types { get { return items.Select(item => item.Type).ToArray(); } }

        public string[] Names { get { return items.Select(item => item.Name).ToArray(); } }

        /// <summary>The entry of the exporter whose attribute has this name, or null.</summary>
        public Item FindEntry(string attributeName)
        {
            return string.IsNullOrEmpty(attributeName) ? null :
                Entries.FirstOrDefault(item => item.AttributeName == attributeName);
        }

        /// <summary>The attribute feature whose attribute has this name, or null.</summary>
        public Item FindAttribute(string attributeName)
        {
            return items.FirstOrDefault(item => item.Attribute != null && item.AttributeName == attributeName);
        }

        public bool HasFolder(string name)
        {
            return items.Any(item => item.Type == "FtrFolder" && item.Name == name);
        }

        /// <summary>Releases <paramref name="value"/> together with the scan. Returns it.</summary>
        public object Keep(object value)
        {
            if (value != null)
            {
                received.Add(value);
            }
            return value;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (object value in received)
            {
                try
                {
                    Release(value);
                }
                catch (Exception)
                {
                    // Releasing never fails an operation
                }
            }
            received.Clear();
            items.Clear();
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
    }

    /// <summary>
    /// The feature tree during one operation: walked once when it is first needed, and walked again
    /// only after <see cref="Changed"/> (a feature was created, deleted, renamed or moved). Objects
    /// from a scan must not be used after <see cref="Changed"/> or <see cref="Dispose"/>.
    /// </summary>
    internal sealed class FeatureTree : IDisposable
    {
        private readonly ModelDoc2 model;
        private FeatureTreeScan scan;

        public FeatureTree(ModelDoc2 model)
        {
            this.model = model;
        }

        public ModelDoc2 Model { get { return model; } }

        public FeatureTreeScan Scan { get { return scan ?? (scan = new FeatureTreeScan(model)); } }

        public void Changed()
        {
            scan?.Dispose();
            scan = null;
        }

        public void Dispose()
        {
            Changed();
        }
    }
}
