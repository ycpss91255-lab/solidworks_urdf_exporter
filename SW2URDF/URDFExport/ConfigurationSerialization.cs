using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.Legacy;
using SW2URDF.URDF;
using SW2URDF.UI;
using SW2URDF.Utilities;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Windows;
using System.Xml;
using System.Xml.Serialization;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Class to serialize URDF trees to string so they can be saved to an SW Attribute in the
    /// top-level assembly document.
    ///
    /// Any changes to the serialization scheme need to support backwards compatibility in some way.
    /// At least in regards to reading the old configuration. I'm also choosing to save any old xml
    /// formats to a second attribute in case they need to be reloaded.
    /// </summary>
    public static class ConfigurationSerialization
    {
        private static readonly log4net.ILog logger = Logger.GetLogger();

        /// <summary>Tests replace it, so they never show a window.</summary>
        private static IMessageBox MessageBox { get { return URDFPackage.MessageBox; } }

        /// <summary>
        /// Current Serialization version
        /// </summary>
        private const double SerializationVersion = ConfigurationFormat.CurrentVersion;

        /// <summary>
        /// The name given to the URDF configuration in the ModelDoc Feature tree. This is displayed to the
        /// user
        /// </summary>
        public const string UrdfConfigurationSwAttributeName = ConfigurationFormat.SharedAttributeName;

        /// <summary>
        /// Definition name of the per-SolidWorks-configuration attribute. One instance is stored per
        /// SolidWorks configuration and is named after that configuration. The serialization format
        /// is the same as the shared attribute (<see cref="SerializationVersion"/>), only the
        /// storage location differs. Note: if a SolidWorks configuration is renamed its attribute no
        /// longer matches; the exporter then offers to move the settings to the new name (see
        /// <see cref="EnsureConfigurationAttributes"/>).
        /// </summary>
        private const string ConfigurationAttributeDefinitionName = "URDF Export Configuration Data";

        /// <summary>
        /// Feature tree folder that groups the per-configuration attributes. It cannot contain a
        /// '.' because SolidWorks rejects such folder names, so it carries no version.
        /// </summary>
        public const string ConfigurationAttributeFolderName = "URDF Export Configuration";
        /// <summary>Folder of older exporter versions; its contents move into the current folder.</summary>
        private const string LegacyItemsFolderName = "URDF Export Items";

        public static List<string> PREVIOUS_URDF_CONFIGURATION_NAMES =
            new List<string>(ConfigurationFormat.PreviousSharedAttributeNames);

        #region Public Methods

        /// <summary>
        /// Loads the URDF tree from the SW Model Document
        /// </summary>
        /// <param name="model">ModelDoc containing the URDF configuration</param>
        /// <returns>TreeView LinkNode loaded from configuration</returns>
        public static LinkNode LoadBaseNodeFromModel(ModelDoc2 model, out bool error, UiText ui = null)
        {
            ui = ui ?? new UiText(null);
            string data;
            double configVersion;
            string entryName;
            using (FeatureTree tree = new FeatureTree(model))
            {
                LogOrphanConfigurationAttributes(tree);
                data = GetConfigTreeData(tree, out configVersion, out entryName);
            }

            ConfigurationFormatKind kind = ConfigurationFormat.Classify(configVersion);
            if (kind == ConfigurationFormatKind.TooNew)
            {
                MessageBox.Show(string.Format(ui.Get("The configuration saved in this model is newer than what this exporter supports ({0} > {1}). Please update your exporter version"), configVersion, SerializationVersion));
                error = true;
                return null;
            }

            error = !TryReadTree(data, configVersion, out LinkNode basenode);
            if (error)
            {
                MessageBox.Show(string.Format(ui.Get("The URDF settings stored in the feature tree entry \"{0}\" cannot be read. They are kept as they are and will not be overwritten."), entryName));
            }
            return basenode;
        }

        /// <summary>
        /// Public method to serialize a Treeview LinkNode URDF data to a string and saves it to a SW ModelDoc
        /// </summary>
        /// <param name="swApp">SldWorks application</param>
        /// <param name="model">ModelDoc to which you are saving this data</param>
        /// <param name="BaseNode">TreeView LinkNode which contains the data you are saving</param>
        /// <param name="warnUser">Warn the user with a YesNo MessageBox, otherwise this will be done silently
        ///  and overwrite any existing data</param>
        public static void SaveConfigTreeXML(SldWorks swApp, ModelDoc2 model, LinkNode BaseNode, bool warnUser, UiText ui = null)
        {
            ui = ui ?? new UiText(null);
            bool stored;
            using (FeatureTree tree = new FeatureTree(model))
            {
                stored = SaveConfigTree(swApp, tree, BaseNode, warnUser, ui);
            }
            if (stored)
            {
                PersistDocument(model, ui);
            }
        }

        /// <summary>True when the settings were written to the document.</summary>
        private static bool SaveConfigTree(SldWorks swApp, FeatureTree tree, LinkNode BaseNode, bool warnUser, UiText ui)
        {
            ModelDoc2 model = tree.Model;
            string oldData = GetConfigTreeData(tree, out double version, out string entryName);
            if (IsOverwrittenBySave(model, entryName) && !TryReadTree(oldData, version, out LinkNode oldTree))
            {
                logger.Error("The URDF settings were not saved: the stored ones cannot be read and are kept");
                MessageBox.Show(string.Format(ui.Get("The URDF settings were not saved: the feature tree entry \"{0}\" holds settings that this exporter cannot read, and they are not overwritten. Delete that entry to store new settings."), entryName));
                return false;
            }
            if (oldData.Length > 0 && ConfigurationFormat.NeedsUpgrade(version))
            {
                MessageBox.Show(ui.Get("You have a URDF configuration with an outdated save format. It will automatically be upgraded to the latest version and saved for the active SolidWorks configuration. The old data is left in place and can be deleted at your convenience."));
                warnUser = false;
            }

            string newData = SerializeToString(BaseNode);
            if (BaseNode != null && string.IsNullOrEmpty(newData))
            {
                MessageBox.Show(ui.Get("Serializing this link failed. Please email your maintainer with your SW assembly."));
                return false;
            }
            if (oldData != newData || !HasConfigurationAttribute(tree))
            {
                if (!warnUser ||
                    (warnUser &&
                    MessageBox.Show(ui.Get("The configuration has changed, would you like to save?"),
                    ui.Get("Save Export Configuration"), MessageBoxButton.YesNo) == MessageBoxResult.Yes))
                {
                    return SaveDataToModelDoc(swApp, tree, newData, ui);
                }
            }
            return false;
        }

        /// <summary>
        /// Makes sure the active SolidWorks configuration has its own URDF attribute (visible under
        /// the "URDF Export Configuration" feature tree folder, named like the configuration). Other
        /// configurations get theirs when the user switches to them and opens the exporter. A
        /// configuration without one gets a copy of the shared (legacy) data, or a blank attribute
        /// when there is none. A configuration without settings is offered the ones of an attribute
        /// that matches no configuration any more (the configuration was renamed); they are moved
        /// when the user says yes. Other existing attributes are never changed. A missing folder is rebuilt
        /// around the attributes that exist. Returns true when anything was created or rebuilt
        /// (the document then needs saving).
        /// </summary>
        public static bool EnsureConfigurationAttributes(SldWorks swApp, ModelDoc2 model, UiText ui = null)
        {
            using (FeatureTree tree = new FeatureTree(model))
            {
                return EnsureConfigurationAttributes(swApp, tree, ui);
            }
        }

        private static bool EnsureConfigurationAttributes(SldWorks swApp, FeatureTree tree, UiText ui)
        {
            ModelDoc2 model = tree.Model;
            string[] names = model.GetConfigurationNames() as string[];
            string active = GetActiveConfigurationName(model);
            HashSet<string> entries = GetEntryNames(tree);
            List<string> existing = (names ?? new string[0]).Where(entries.Contains).ToList();

            bool created = false;
            string[] toCreate = DocumentPersistence.NamesToCreate(names, active, existing.ToArray());
            if (AdoptOrphan(swApp, tree, names, active, ui ?? new UiText(null)))
            {
                created = true;
            }
            else if (toCreate.Length > 0 && !HasConfigurationAttribute(tree))
            {
                SolidWorks.Interop.sldworks.Attribute shared = FindSharedAttribute(tree);
                string sharedData = shared == null ? "" : shared.GetParameter("data").GetStringValue();
                double sharedVersion = shared == null ? SerializationVersion : shared.GetParameter("exporterVersion").GetDoubleValue();
                AttributeInit init = DocumentPersistence.DecideAttribute(false, !string.IsNullOrEmpty(sharedData));

                SolidWorks.Interop.sldworks.Attribute att = CreateConfigurationAttribute(swApp, model, active);
                if (att == null)
                {
                    logger.Warn("Could not create the URDF attribute for configuration \"" + active + "\"");
                }
                else
                {
                    bool copy = init == AttributeInit.CopyShared;
                    WriteAttribute(att, copy ? sharedData : "", copy ? sharedVersion : SerializationVersion);
                    created = true;
                }
                tree.Changed();
            }

            bool removed = RemoveMigratedSharedAttributes(tree);
            bool regrouped = RegroupFolder(tree, active, ui);
            return created || regrouped || removed;
        }

        /// <summary>The "date" parameter of a URDF attribute: when its settings were written (ISO 8601, not read back).</summary>
        internal static string AttributeDate(DateTimeOffset time)
        {
            return time.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// When the active configuration has no settings of its own, asks whether it takes over the
        /// settings of an attribute that matches no configuration. Attribute values belong to the
        /// configuration they were written for and stay with it when it is renamed, so only an
        /// attribute that holds data for the active configuration is offered. True when the settings
        /// were moved.
        /// </summary>
        private static bool AdoptOrphan(SldWorks swApp, FeatureTree tree, string[] names, string active, UiText ui)
        {
            if (names == null || string.IsNullOrEmpty(active) || !names.Contains(active))
            {
                return false;
            }
            SolidWorks.Interop.sldworks.Attribute own = FindConfigurationAttribute(tree, active);
            if (own != null && !string.IsNullOrEmpty(own.GetParameter("data").GetStringValue()))
            {
                return false;
            }
            List<FeatureTreeScan.Item> attributes = tree.Scan.Entries
                .Where(item => item.AttributeName != null && !IsSharedAttributeName(item.AttributeName)).ToList();
            foreach (string orphan in OrphanAttributes.FindOrphans(attributes.Select(item => item.AttributeName), names))
            {
                SolidWorks.Interop.sldworks.Attribute source = attributes.First(item => item.AttributeName == orphan).Attribute;
                string data = source.GetParameter("data").GetStringValue();
                if (string.IsNullOrEmpty(data))
                {
                    continue;
                }
                bool adopt = MessageBox.Show(
                    string.Format(ui.Get("The configuration \"{0}\" has no URDF settings of its own. The settings stored under the name \"{1}\" belong to no configuration any more, which happens when a configuration is renamed. Use them for \"{0}\"?"), active, orphan),
                    ui.Get("Configuration renamed"), MessageBoxButton.YesNo) == MessageBoxResult.Yes;
                logger.Info("URDF settings of \"" + orphan + "\" offered to \"" + active + "\", accepted: " + adopt);
                if (adopt)
                {
                    bool create = own == null;
                    own = own ?? CreateConfigurationAttribute(swApp, tree.Model, active);
                    bool moved = own != null && MoveSettings(source, own);
                    if (create)
                    {
                        tree.Changed();
                    }
                    return moved;
                }
            }
            return false;
        }

        /// <summary>The source is only emptied after the target holds its data.</summary>
        private static bool MoveSettings(
            SolidWorks.Interop.sldworks.Attribute source, SolidWorks.Interop.sldworks.Attribute target)
        {
            string data = source.GetParameter("data").GetStringValue();
            double version = source.GetParameter("exporterVersion").GetDoubleValue();
            WriteAttribute(target, data, version);
            if (target.GetParameter("data").GetStringValue() != data ||
                target.GetParameter("exporterVersion").GetDoubleValue() != version)
            {
                logger.Warn("The URDF settings of \"" + source.GetName() + "\" could not be moved, they are kept there");
                return false;
            }
            source.GetParameter("data").SetStringValue2(
                "", (int)swInConfigurationOpts_e.swThisConfiguration, "");
            return true;
        }

        /// <summary>Writes the values of the active configuration.</summary>
        private static void WriteAttribute(SolidWorks.Interop.sldworks.Attribute att, string data, double version)
        {
            int options = (int)swInConfigurationOpts_e.swThisConfiguration;
            att.GetParameter("data").SetStringValue2(data, options, "");
            att.GetParameter("name").SetStringValue2("config1", options, "");
            att.GetParameter("date").SetStringValue2(AttributeDate(DateTimeOffset.Now), options, "");
            att.GetParameter("exporterVersion").SetDoubleValue2(version, options, "");
        }

        /// <summary>
        /// The old shared attributes are only a fallback for configurations without an entry of
        /// their own. Once every configuration has its own entry with the same settings they are
        /// deleted (see <see cref="SharedAttributeCleanup"/>). Never throws. True when the tree changed.
        /// </summary>
        private static bool RemoveMigratedSharedAttributes(FeatureTree tree)
        {
            try
            {
                return SharedAttributeCleanup.Run(
                    new ModelSharedAttributes(tree), CanonicalTree, message => logger.Info(message)) ==
                    SharedCleanupResult.Removed;
            }
            catch (Exception e)
            {
                logger.Warn("Checking the old shared URDF attributes failed, they are kept", e);
                return false;
            }
        }

        /// <summary>Canonical text of the tree stored in <paramref name="data"/>, or null when it cannot be read.</summary>
        private static string CanonicalTree(string data, double version)
        {
            try
            {
                return TryReadTree(data, version, out LinkNode node) && node != null ? SerializeToString(node) : null;
            }
            catch (Exception e)
            {
                logger.Warn("Could not read URDF settings while comparing them", e);
                return null;
            }
        }

        private sealed class ModelSharedAttributes : ISharedAttributeDocument
        {
            private readonly FeatureTree tree;
            private readonly ModelDoc2 model;

            public ModelSharedAttributes(FeatureTree tree)
            {
                this.tree = tree;
                model = tree.Model;
            }

            public string[] ConfigurationNames()
            {
                return model.GetConfigurationNames() as string[];
            }

            public bool HasEntry(string configuration)
            {
                return tree.Scan.FindEntry(configuration) != null;
            }

            public AttributeData GetEntry(string configuration)
            {
                FeatureTreeScan scan = tree.Scan;
                FeatureTreeScan.Item item = scan.FindEntry(configuration);
                if (item == null)
                {
                    return null;
                }
                // SolidWorks only reads an attribute value for the active configuration, so the
                // configuration is shown for a moment and the previous one restored right after
                string active = GetActiveConfigurationName(model);
                bool switched = configuration != active;
                try
                {
                    if (switched && !model.ShowConfiguration2(configuration))
                    {
                        return null;
                    }
                    // The attribute is asked for again once the configuration is shown
                    return Read(scan.Keep(item.Feature.GetSpecificFeature2()) as SolidWorks.Interop.sldworks.Attribute);
                }
                finally
                {
                    if (switched && active != null)
                    {
                        model.ShowConfiguration2(active);
                    }
                }
            }

            public AttributeData GetSharedFallback()
            {
                return Read(FindSharedAttribute(tree));
            }

            private static AttributeData Read(SolidWorks.Interop.sldworks.Attribute attribute)
            {
                return attribute == null ? null : new AttributeData(
                    attribute.GetParameter("data").GetStringValue(),
                    attribute.GetParameter("exporterVersion").GetDoubleValue());
            }

            public bool DeleteSharedAttributes()
            {
                // Only attributes of the exporter's own definition (they carry data and a format
                // version), found by their shared names. Features of the user are never touched.
                List<FeatureTreeScan.Item> shared = SharedEntries(tree.Scan);
                foreach (FeatureTreeScan.Item item in shared)
                {
                    model.ClearSelection2(true);
                    item.Feature.Select2(false, 0);
                    model.Extension.DeleteSelection2(0);
                }
                model.ClearSelection2(true);
                if (shared.Count > 0)
                {
                    tree.Changed();
                }
                return SharedEntries(tree.Scan).Count == 0;
            }

            private static List<FeatureTreeScan.Item> SharedEntries(FeatureTreeScan scan)
            {
                return scan.Entries.Where(item => item.AttributeName != null && IsSharedAttributeName(item.AttributeName)).ToList();
            }
        }

        /// <summary>
        /// Where the settings for the active SolidWorks configuration come from (what
        /// <see cref="LoadBaseNodeFromModel"/> loads), without loading them.
        /// </summary>
        public static SettingsSource GetSettingsSource(ModelDoc2 model)
        {
            bool hasOwn = false;
            bool hasShared = false;
            try
            {
                using (FeatureTree tree = new FeatureTree(model))
                {
                    SolidWorks.Interop.sldworks.Attribute att =
                        FindConfigurationAttribute(tree, GetActiveConfigurationName(model));
                    hasOwn = att != null && !string.IsNullOrEmpty(att.GetParameter("data").GetStringValue());
                    if (!hasOwn)
                    {
                        SolidWorks.Interop.sldworks.Attribute shared = FindSharedAttribute(tree);
                        hasShared = shared != null && !string.IsNullOrEmpty(shared.GetParameter("data").GetStringValue());
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn("Could not determine where the URDF settings come from", e);
            }
            return ConfigurationStatus.Decide(hasOwn, hasShared);
        }

        /// <summary>Localized one-line status: active configuration and where its settings come from.</summary>
        public static string DescribeSettings(ModelDoc2 model, UiText ui)
        {
            return ConfigurationStatus.Describe(
                ui, GetActiveConfigurationName(model), GetSettingsSource(model));
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// If someone updates the name of a LinkNode in the Treeview, it needs to be pushed down
        /// to the URDF Link itself.
        /// </summary>
        /// <param name="node">TreeView LinkNode to save properties of to its URDF Link</param>
        private static void SavePropertiesLinkNodeToLink(LinkNode node)
        {
            if (node.Link == null)
            {
                node.Link = new Link();
                return;
            }

            node.Link.Name = node.Name;

            foreach (LinkNode child in node.Nodes)
            {
                SavePropertiesLinkNodeToLink(child);
            }
        }

        /// <summary>
        /// Data Contract serialization. All members of an object need to be annotated with a
        /// [DataMember] attribute.
        /// </summary>
        /// <param name="node">TreeView LinkNode to serialize</param>
        /// <returns>A string serialized utilizing DataContract serialization XML scheme</returns>
        private static string SerializeToString(LinkNode node)
        {
            SavePropertiesLinkNodeToLink(node);
            Link link = node.UpdateLinkTree(null);
            string data = "";
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractSerializer ser =
                    new DataContractSerializer(typeof(Link));

                try
                {
                    ser.WriteObject(stream, link);
                    stream.Flush();
                    data = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Position);
                }
                catch (SerializationException e)
                {
                    logger.Error("Serialization failed with exception, returning empty string", e);
                }
            }
            return data;
        }

        /// <summary>
        /// Reads stored settings. False when there are settings that cannot be read: they must then
        /// be kept. No data at all is readable and gives no tree.
        /// </summary>
        private static bool TryReadTree(string data, double version, out LinkNode node)
        {
            node = null;
            if (string.IsNullOrWhiteSpace(data))
            {
                return true;
            }
            ConfigurationFormatKind kind = ConfigurationFormat.Classify(version);
            if (kind == ConfigurationFormatKind.TooNew)
            {
                return false;
            }
            try
            {
                node = kind == ConfigurationFormatKind.DataContract ?
                    DeserializeFromString(data) : LoadConfigFromStringXML(data);
                return node != null;
            }
            catch (Exception e)
            {
                logger.Error("Reading the stored URDF settings failed", e);
                logger.Error(data);
                return false;
            }
        }

        /// <summary>True when a save writes over the attribute the settings were read from.</summary>
        private static bool IsOverwrittenBySave(ModelDoc2 model, string entryName)
        {
            return entryName == (GetActiveConfigurationName(model) ?? UrdfConfigurationSwAttributeName);
        }

        /// <summary>
        /// Read a URDF Link from a serialized string. Throws when it cannot be read.
        /// </summary>
        /// <param name="data">Data string to read into a TreeView LinkNode</param>
        /// <returns>Deserialized LinkNode</returns>
        private static LinkNode DeserializeFromString(string data)
        {
            LinkNode baseNode = null;
            if (!string.IsNullOrWhiteSpace(data))
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(data)))
                {
                    DataContractSerializer ser =
                        new DataContractSerializer(typeof(Link));

                    Link link = (Link)ser.ReadObject(stream);

                    // By copying this link, we can ensure that all non-serialized properties are setup correctly
                    Link copy = link.Clone();
                    baseNode = new LinkNode(copy);
                }
            }
            return baseNode;
        }

        /// <summary>
        /// Load from the deprecated XML serialized scheme. Throws when it cannot be read.
        /// </summary>
        /// <param name="data">Data string to deserialize using XMLSerializer</param>
        /// <returns>TreeView LinkNode</returns>
        private static LinkNode LoadConfigFromStringXML(string data)
        {
            LinkNode baseNode = null;
            if (!string.IsNullOrWhiteSpace(data))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(SerialNode));
                using (XmlTextReader textReader = new XmlTextReader(new StringReader(data)))
                {
                    // Not reading external files, so this can set to prohibit. Resolves CA3075
                    textReader.DtdProcessing = DtdProcessing.Prohibit;
                    SerialNode sNode = (SerialNode)serializer.Deserialize(textReader);
                    baseNode = sNode.BuildLinkNodeFromSerialNode();
                }
            }
            return baseNode;
        }

        /// <summary>
        /// The shared attribute of the current format, else one of an older format, else null.
        /// </summary>
        private static SolidWorks.Interop.sldworks.Attribute FindSharedAttribute(FeatureTree tree)
        {
            FeatureTreeScan scan = tree.Scan;
            foreach (string name in new[] { UrdfConfigurationSwAttributeName }.Concat(PREVIOUS_URDF_CONFIGURATION_NAMES))
            {
                FeatureTreeScan.Item item = scan.FindAttribute(name);
                if (item != null)
                {
                    return item.Attribute;
                }
            }
            return null;
        }

        /// <summary>
        /// Find the SW attribute that contains the URDF configuration serialized string
        /// </summary>
        /// <param name="tree">Feature tree of the document to load the URDF configuration from</param>
        /// <param name="version">Output parameter of the serialization version</param>
        /// <param name="entryName">Name of the attribute the data comes from, null when there is none</param>
        /// <returns>Serialized data string</returns>
        private static string GetConfigTreeData(FeatureTree tree, out double version, out string entryName)
        {
            string data = "";
            version = 0.0;
            entryName = null;

            // Prefer the attribute stored for the active SolidWorks configuration
            SolidWorks.Interop.sldworks.Attribute configAtt =
                FindConfigurationAttribute(tree, GetActiveConfigurationName(tree.Model));
            if (configAtt != null)
            {
                string configData = configAtt.GetParameter("data").GetStringValue();
                if (!string.IsNullOrEmpty(configData))
                {
                    logger.Info("URDF Configuration found for the active SolidWorks configuration\n" + configData);
                    version = configAtt.GetParameter("exporterVersion").GetDoubleValue();
                    entryName = configAtt.GetName();
                    return configData;
                }
            }

            // Otherwise fall back to the shared attribute, so older models keep working unchanged:
            // the most recent serialization version first, then the older ones
            SolidWorks.Interop.sldworks.Attribute swAtt = FindSharedAttribute(tree);
            if (swAtt != null)
            {
                Parameter param = swAtt.GetParameter("data");
                data = param.GetStringValue();
                logger.Info("URDF Configuration found\n" + data);

                param = swAtt.GetParameter("exporterVersion");
                version = param.GetDoubleValue();
                entryName = swAtt.GetName();
            }

            return data;
        }

        /// <summary>
        /// Detection only (issue #11): logs per-configuration attributes that match no
        /// configuration. Nothing is changed and loading is never blocked.
        /// </summary>
        private static void LogOrphanConfigurationAttributes(FeatureTree tree)
        {
            try
            {
                // Instances of the shared attribute definitions also carry data/exporterVersion
                List<string> attributeNames = tree.Scan.Entries
                    .Where(item => item.AttributeName != null && !IsSharedAttributeName(item.AttributeName))
                    .Select(item => item.AttributeName).ToList();
                string[] configNames = tree.Model.GetConfigurationNames() as string[];
                List<string> orphans = OrphanAttributes.FindOrphans(attributeNames, configNames);
                string message = OrphanAttributes.BuildLogMessage(
                    orphans, OrphanAttributes.FindConfigurationsWithoutAttribute(attributeNames, configNames));
                if (message != null)
                {
                    logger.Warn(message);
                }
            }
            catch (Exception e)
            {
                logger.Warn("Checking for orphan URDF configuration attributes failed", e);
            }
        }

        private static bool IsSharedAttributeName(string name)
        {
            return name == ConfigurationFormat.SharedAttributeName ||
                ConfigurationFormat.PreviousSharedAttributeNames.Contains(name);
        }

        /// <summary>
        /// False, with a message, when another configuration was activated since the exporter was
        /// opened for <paramref name="opened"/>: its settings must not be replaced by the ones of
        /// that configuration. A configuration that was renamed meanwhile is still the same one.
        /// </summary>
        public static bool CheckSameConfiguration(ModelDoc2 model, string opened, UiText ui)
        {
            if (opened == null)
            {
                return true;
            }
            string active = GetActiveConfigurationName(model);
            string[] names = model.GetConfigurationNames() as string[];
            if (active == null || active == opened || names == null || !names.Contains(opened))
            {
                return true;
            }
            logger.Warn("The exporter was opened for configuration \"" + opened + "\", now \"" + active +
                "\" is active: nothing is saved or exported");
            MessageBox.Show(string.Format(ui.Get("The exporter was opened for the configuration \"{0}\", but now \"{1}\" is active. Nothing was saved or exported, so that the settings of \"{1}\" are kept. Activate \"{0}\" again, or open the exporter again for \"{1}\"."), opened, active));
            return false;
        }

        public static string GetActiveConfigurationName(ModelDoc2 model)
        {
            try
            {
                Configuration configuration = model.ConfigurationManager?.ActiveConfiguration;
                string name = configuration?.Name;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception e)
            {
                logger.Warn("Could not read the active configuration name", e);
                return null;
            }
        }

        /// <summary>The names of all attributes of the exporter.</summary>
        private static HashSet<string> GetEntryNames(FeatureTree tree)
        {
            return new HashSet<string>(tree.Scan.Entries.Select(item => item.AttributeName));
        }

        /// <summary>The entry of the configuration, or null.</summary>
        private static SolidWorks.Interop.sldworks.Attribute FindConfigurationAttribute(FeatureTree tree, string configurationName)
        {
            return string.IsNullOrEmpty(configurationName) ? null : tree.Scan.FindEntry(configurationName)?.Attribute;
        }

        /// <summary>
        /// After an export: the URDF folder holds only configuration entries (the generated reference sketch,
        /// coordinate systems and axes stay at the feature tree root). Saves the document if the tree changed. Never throws.
        /// </summary>
        public static void GroupUrdfFeatures(ModelDoc2 model, UiText ui)
        {
            try
            {
                bool changed;
                using (FeatureTree tree = new FeatureTree(model))
                {
                    changed = RegroupFolder(tree, GetActiveConfigurationName(model), ui);
                }
                if (changed)
                {
                    PersistDocument(model, ui ?? new UiText(null));
                }
            }
            catch (Exception e)
            {
                logger.Warn("Could not tidy the URDF folder after the export", e);
            }
        }

        /// <summary>
        /// Shows the active configuration's entry in the folder (rebuilding a missing folder) and
        /// keeps what the folder already shows. Entries of other configurations stay hidden outside
        /// the folder, with their data, until that configuration is used. True when the tree changed.
        /// </summary>
        private static bool RegroupFolder(FeatureTree tree, string active, UiText ui)
        {
            bool changed = ResolveFolderNameCollision(tree, out bool blocked);
            changed |= DeleteStrayFolders(tree);
            if (blocked)
            {
                logger.Warn("A feature that is not ours is named \"" + ConfigurationAttributeFolderName +
                    "\", so the URDF features are left where they are");
                return changed;
            }
            FeatureTreeScan scan = tree.Scan;
            string[] inFolder = GetNamesInFolder(scan, ConfigurationAttributeFolderName);
            string[] existing = scan.Entries.Select(item => item.AttributeName).ToArray();
            string[] toShow = DocumentPersistence.WithExtras(
                DocumentPersistence.NamesToGroup(inFolder, active, existing), GetOtherUrdfFeatureNames(scan, false));
            bool folderExisted = scan.HasFolder(ConfigurationAttributeFolderName);
            bool legacyFolder = GetNamesInFolder(scan, LegacyItemsFolderName).Length > 0 || scan.HasFolder(LegacyItemsFolderName);
            if (!DocumentPersistence.NeedsRegroup(inFolder, toShow, folderExisted) && !legacyFolder)
            {
                return changed;
            }
            TryGroupConfigurationAttributes(tree, toShow);
            if (toShow.Length > 0 && !tree.Scan.HasFolder(ConfigurationAttributeFolderName))
            {
                WarnFolderMissing(tree.Model, ui ?? new UiText(null));
            }
            return true;
        }

        private static readonly HashSet<string> documentsWarnedAboutFolder = new HashSet<string>();

        /// <summary>
        /// The folder was deleted and SolidWorks did not create it again. The settings are still in
        /// the entries and the next regroup tries again, so the user is told once per document.
        /// </summary>
        private static void WarnFolderMissing(ModelDoc2 model, UiText ui)
        {
            logger.Warn("The feature tree folder \"" + ConfigurationAttributeFolderName +
                "\" is missing, its entries are kept in the feature tree");
            if (documentsWarnedAboutFolder.Add(model.GetPathName() ?? ""))
            {
                MessageBox.Show(string.Format(ui.Get("SolidWorks could not create the feature tree folder \"{0}\". The URDF settings are kept in their entries in the feature tree. The exporter creates the folder the next time it is opened or the settings are saved."), ConfigurationAttributeFolderName));
            }
        }

        private static FeatureTreeScan.Item FindFolder(FeatureTreeScan scan, string name)
        {
            return scan.Items.FirstOrDefault(item => item.Type == "FtrFolder" && item.Name == name);
        }

        /// <summary>
        /// Feature names are unique, so an old exporter attribute called like the folder
        /// ("URDF Export Configuration") would leave the folder with its default name. That attribute
        /// is renamed (its data stays, it is still read as an old shared attribute). A feature of the
        /// user with the name is never touched: <paramref name="blocked"/> is then true.
        /// True when the document changed.
        /// </summary>
        private static bool ResolveFolderNameCollision(FeatureTree tree, out bool blocked)
        {
            blocked = false;
            FeatureTreeScan scan = tree.Scan;
            string[] names = scan.Names;
            FolderNameCollision collision =
                DocumentPersistence.DecideFolderNameCollision(scan.Types, names, ConfigurationAttributeFolderName);
            if (collision == FolderNameCollision.Blocked)
            {
                blocked = true;
                return false;
            }
            if (collision == FolderNameCollision.None)
            {
                return false;
            }
            FeatureTreeScan.Item attribute = scan.Items.FirstOrDefault(item =>
                item.Type == "Attribute" && item.Name == ConfigurationAttributeFolderName);
            if (attribute == null)
            {
                return false;
            }
            string target = DocumentPersistence.FreeName(DocumentPersistence.LegacyAttributeRenamedName, names);
            try
            {
                attribute.Feature.Name = target;
            }
            catch (Exception e)
            {
                logger.Warn("Could not rename the old URDF attribute", e);
            }
            if (attribute.Feature.Name == target)
            {
                logger.Info("Renamed the old URDF attribute to \"" + target + "\"");
                tree.Changed();
                return true;
            }
            blocked = true;
            return false;
        }

        /// <summary>
        /// Removes the default-named folders ("Folder1") that earlier versions left around the URDF
        /// features. Only folders holding nothing but exporter features; their contents are kept.
        /// True when the document changed.
        /// </summary>
        private static bool DeleteStrayFolders(FeatureTree tree)
        {
            ModelDoc2 model = tree.Model;
            FeatureTreeScan scan = tree.Scan;
            List<string> ours = new List<string>(GetOtherUrdfFeatureNames(scan, true));
            ours.AddRange(scan.Entries.Select(item => item.Name));
            string[] strays = DocumentPersistence.FindStrayFolders(scan.Types, scan.Names, (t, n) => ours.Contains(n));
            bool changed = false;
            foreach (string name in strays)
            {
                FeatureTreeScan.Item folder = FindFolder(scan, name);
                if (folder != null)
                {
                    model.ClearSelection2(true);
                    folder.Feature.Select2(false, 0);
                    // 0 = delete only the folder, its contents are kept
                    changed |= model.Extension.DeleteSelection2(0);
                }
            }
            model.ClearSelection2(true);
            if (strays.Length > 0)
            {
                tree.Changed();
            }
            return changed;
        }

        /// <summary>
        /// Other exporter features besides the configuration entries: the shared (legacy) attributes, which
        /// still go into the folder, and, only when includeGenerated is set (stray folder check), the generated
        /// features and the older "URDF Export Items" folder contents, which stay loose.
        /// </summary>
        private static string[] GetOtherUrdfFeatureNames(FeatureTreeScan scan, bool includeGenerated)
        {
            List<string> names = new List<string>();
            bool inLegacy = false;
            foreach (FeatureTreeScan.Item item in scan.Items)
            {
                string type = item.Type;
                string name = item.Name;
                if (type == "FtrFolder")
                {
                    if (name == LegacyItemsFolderName)
                    {
                        inLegacy = true;
                    }
                    else if (inLegacy && name.EndsWith("___EndTag___"))
                    {
                        inLegacy = false;
                    }
                }
                else if (type == "Attribute")
                {
                    if (item.AttributeName != null && (IsSharedAttributeName(item.AttributeName) || inLegacy))
                    {
                        names.Add(name);
                    }
                }
                else if (includeGenerated && (DocumentPersistence.IsGeneratedUrdfFeature(type, name) || inLegacy))
                {
                    names.Add(name);
                }
            }
            return names.ToArray();
        }

        /// <summary>Names of the features that sit inside the folder (between the folder and its end tag).</summary>
        private static string[] GetNamesInFolder(FeatureTreeScan scan, string folderName)
        {
            List<string> names = new List<string>();
            bool inside = false;
            foreach (FeatureTreeScan.Item item in scan.Items)
            {
                if (item.Type == "FtrFolder")
                {
                    if (item.Name == folderName)
                    {
                        inside = true;
                    }
                    else if (inside && item.Name.EndsWith("___EndTag___"))
                    {
                        break;
                    }
                }
                else if (inside)
                {
                    names.Add(item.Name);
                }
            }
            return names.ToArray();
        }

        private static bool HasConfigurationAttribute(FeatureTree tree)
        {
            string name = GetActiveConfigurationName(tree.Model);
            return name != null && FindConfigurationAttribute(tree, name) != null;
        }

        /// <summary>
        /// Appearance only: put all per-configuration attributes in one feature tree folder.
        /// SolidWorks cannot move a feature into an existing folder, so the old folder is deleted
        /// (deleting a folder keeps its contents) and recreated around all attributes, with a second
        /// try when SolidWorks does not create it. Failures are logged and never interrupt saving;
        /// the caller checks whether the folder exists.
        /// </summary>
        private static void TryGroupConfigurationAttributes(FeatureTree tree, string[] namesToGroup)
        {
            ModelDoc2 model = tree.Model;
            try
            {
                FeatureTreeScan scan = tree.Scan;
                List<Feature> features = scan.Items
                    .Where(item => item.Type != "FtrFolder" && Array.IndexOf(namesToGroup, item.Name) >= 0)
                    .Select(item => item.Feature).ToList();

                // Both folders go (deleting a folder keeps its contents): the current one, which is
                // recreated below, and the older "URDF Export Items" one, whose contents move in.
                foreach (string folderName in new[] { ConfigurationAttributeFolderName, LegacyItemsFolderName })
                {
                    FeatureTreeScan.Item folder = FindFolder(scan, folderName);
                    if (folder != null)
                    {
                        model.ClearSelection2(true);
                        folder.Feature.Select2(false, 0);
                        // 0 = delete only the folder, its contents are kept
                        model.Extension.DeleteSelection2(0);
                    }
                }
                if (features.Count == 0)
                {
                    return;
                }

                Feature newFolder = InsertFolderAround(model, features) ?? InsertFolderAround(model, features);
                if (newFolder != null)
                {
                    scan.Keep(newFolder);
                    try
                    {
                        newFolder.Name = ConfigurationAttributeFolderName;
                    }
                    catch (Exception e)
                    {
                        logger.Warn("Could not name the URDF feature tree folder", e);
                    }
                    if (newFolder.Name != ConfigurationAttributeFolderName)
                    {
                        // Never leave a default-named folder behind: delete it, its contents stay
                        logger.Warn("The URDF feature tree folder kept the name \"" + newFolder.Name + "\", removing it");
                        model.ClearSelection2(true);
                        newFolder.Select2(false, 0);
                        model.Extension.DeleteSelection2(0);
                    }
                }
                else
                {
                    logger.Warn("Could not create the URDF feature tree folder");
                }
                model.ClearSelection2(true);
            }
            catch (Exception e)
            {
                logger.Warn("Grouping the URDF features into a folder failed", e);
            }
            finally
            {
                tree.Changed();
            }
        }

        /// <summary>The new folder, or null when SolidWorks does not create it. Never throws.</summary>
        private static Feature InsertFolderAround(ModelDoc2 model, List<Feature> features)
        {
            try
            {
                model.ClearSelection2(true);
                bool append = false;
                foreach (Feature feature in features)
                {
                    feature.Select2(append, 0);
                    append = true;
                }
                return model.FeatureManager.InsertFeatureTreeFolder2(
                    (int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing) as Feature;
            }
            catch (Exception e)
            {
                logger.Warn("Inserting the URDF feature tree folder failed", e);
                return null;
            }
        }

        /// <summary>
        /// Builds a SW Attribute for saving our serialized data
        /// </summary>
        /// <param name="swApp">SolidWorks Application to build Feature Definition</param>
        /// <param name="model">ModelDoc in which this attribute will be saved</param>
        /// <param name="name">Name of the attribute to create</param>
        /// <returns>Constructed SolidWorks Attribute</returns>
        private static SolidWorks.Interop.sldworks.Attribute
            CreateSWSaveAttribute(SldWorks swApp, FeatureTree tree, string name)
        {
            FeatureTreeScan.Item existing = tree.Scan.FindAttribute(name);
            if (existing != null)
            {
                return existing.Attribute;
            }
            ModelDoc2 model = tree.Model;

            int ConfigurationOptions = (int)swInConfigurationOpts_e.swAllConfiguration;

            int Options = 0;
            AttributeDef saveConfigurationAttributeDef;
            saveConfigurationAttributeDef = swApp.DefineAttribute(UrdfConfigurationSwAttributeName);

            saveConfigurationAttributeDef.AddParameter(
                "data", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "name", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "date", (int)swParamType_e.swParamTypeString, 0, Options);
            saveConfigurationAttributeDef.AddParameter(
                "exporterVersion", (int)swParamType_e.swParamTypeDouble, SerializationVersion, Options);
            saveConfigurationAttributeDef.Register();

            SolidWorks.Interop.sldworks.Attribute saveExporterAttribute =
                saveConfigurationAttributeDef.CreateInstance5(
                    model, null, UrdfConfigurationSwAttributeName, Options, ConfigurationOptions);
            tree.Changed();
            return saveExporterAttribute;
        }

        /// <summary>
        /// Saves a string of data to the SWModelDoc
        /// </summary>
        /// <param name="swApp">SolidWorks Application</param>
        /// <param name="tree">Feature tree of the document to save the data string to</param>
        /// <param name="data">string to save</param>
        private static bool SaveDataToModelDoc(SldWorks swApp, FeatureTree tree,
            string data, UiText ui)
        {
            ModelDoc2 model = tree.Model;
            string configurationName = GetActiveConfigurationName(model);
            int ConfigurationOptions = (int)swInConfigurationOpts_e.swAllConfiguration;
            SolidWorks.Interop.sldworks.Attribute saveExporterAttribute;
            if (configurationName == null)
            {
                // No usable active configuration: keep the shared attribute behavior
                saveExporterAttribute =
                    CreateSWSaveAttribute(swApp, tree, UrdfConfigurationSwAttributeName);
            }
            else
            {
                ConfigurationOptions = (int)swInConfigurationOpts_e.swThisConfiguration;
                saveExporterAttribute = FindConfigurationAttribute(tree, configurationName);
                if (saveExporterAttribute == null)
                {
                    saveExporterAttribute = CreateConfigurationAttribute(swApp, model, configurationName);
                    tree.Changed();
                }
            }

            if (saveExporterAttribute == null)
            {
                logger.Error("SolidWorks did not create the URDF configuration attribute" +
                    (configurationName == null ? "" : " for configuration \"" + configurationName + "\""));
                MessageBox.Show(ui.Get("The URDF configuration could not be saved to this model."));
                return false;
            }

            Parameter param = saveExporterAttribute.GetParameter("data");
            param.SetStringValue2(data, ConfigurationOptions, "");
            param = saveExporterAttribute.GetParameter("name");
            param.SetStringValue2("config1", ConfigurationOptions, "");
            param = saveExporterAttribute.GetParameter("date");
            param.SetStringValue2(AttributeDate(DateTimeOffset.Now), ConfigurationOptions, "");
            param = saveExporterAttribute.GetParameter("exporterVersion");
            param.SetDoubleValue2(SerializationVersion, ConfigurationOptions, "");

            RegroupFolder(tree, configurationName, ui);
            return true;
        }

        /// <summary>
        /// The settings live in the open document: without saving it they are lost as soon as the
        /// document is closed. Saves a document that has a file (silently, only this document, not
        /// its references); otherwise tells the user to save it.
        /// </summary>
        public static void PersistDocument(ModelDoc2 model, UiText ui)
        {
            int saveErrors = 0;
            int saveWarnings = 0;
            bool saved = false;
            PersistAction action = DocumentPersistence.Decide(
                true, !string.IsNullOrEmpty(model.GetPathName()), model.IsOpenedReadOnly());
            if (action == PersistAction.Save)
            {
                try
                {
                    saved = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
                }
                catch (Exception e)
                {
                    logger.Warn("Saving the document after storing the URDF settings failed", e);
                }
                logger.Info("Saved the document after storing the URDF settings: " + saved + " (errors " + saveErrors + ", warnings " + saveWarnings + ")");
            }
            if (action == PersistAction.WarnNotSaved || (action == PersistAction.Save && !saved))
            {
                MessageBox.Show(DocumentPersistence.WarningText(ui), DocumentPersistence.WarningTitle(ui), MessageBoxButton.OK);
            }
            else if (action == PersistAction.Save)
            {
                // Saved, but SolidWorks reported errors or warnings: do not pretend all is well
                DocumentPersistence.ReportSaveProblem(ui, saved, saveErrors, saveWarnings);
            }
        }

        private static SolidWorks.Interop.sldworks.Attribute
            CreateConfigurationAttribute(SldWorks swApp, ModelDoc2 model, string configurationName)
        {
            int Options = 0;
            AttributeDef attributeDef = swApp.DefineAttribute(ConfigurationAttributeDefinitionName);
            attributeDef.AddParameter(
                "data", (int)swParamType_e.swParamTypeString, 0, Options);
            attributeDef.AddParameter(
                "name", (int)swParamType_e.swParamTypeString, 0, Options);
            attributeDef.AddParameter(
                "date", (int)swParamType_e.swParamTypeString, 0, Options);
            attributeDef.AddParameter(
                "exporterVersion", (int)swParamType_e.swParamTypeDouble, SerializationVersion, Options);
            attributeDef.Register();

            return attributeDef.CreateInstance5(
                model, null, configurationName, Options,
                (int)swInConfigurationOpts_e.swThisConfiguration);
        }

        #endregion Private Methods
    }
}