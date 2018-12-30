using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CKAN.Versioning;
using log4net;

namespace CKAN
{
    /// <summary>
    /// Manage multiple KSP installs.
    /// </summary>
    public class KSPManager : IDisposable
    {
        public IUser User { get; set; }
        public IWin32Registry Win32Registry { get; set; }
        public KSP CurrentInstance { get; set; }

        public NetModuleCache Cache { get; private set; }

        private static readonly ILog log = LogManager.GetLogger(typeof (KSPManager));

        private readonly SortedList<string, KSP> instances = new SortedList<string, KSP>();

        public string AutoStartInstance
        {
            get { return Win32Registry.AutoStartInstance; }
            private set
            {
                if (!String.IsNullOrEmpty(value) && !HasInstance(value))
                {
                    throw new InvalidKSPInstanceKraken(value);
                }
                Win32Registry.AutoStartInstance = value;
            }
        }

        public SortedList<string, KSP> Instances
        {
            get { return new SortedList<string, KSP>(instances); }
        }

        public KSPManager(IUser user, IWin32Registry win32_registry = null)
        {
            User = user;
            Win32Registry = win32_registry ?? new Win32Registry();
            LoadInstancesFromRegistry();
        }

        /// <summary>
        /// Returns the prefered KSP instance, or null if none can be found.
        ///
        /// This works by checking to see if we're in a KSP dir first, then the
        /// registry for an autostart instance, then will try to auto-populate
        /// by scanning for the game.
        ///
        /// This *will not* touch the registry if we find a portable install.
        ///
        /// This *will* run KSP instance autodetection if the registry is empty.
        ///
        /// This *will* set the current instance, or throw an exception if it's already set.
        ///
        /// Returns null if we have multiple instances, but none of them are preferred.
        /// </summary>
        public KSP GetPreferredInstance()
        {
            CurrentInstance = _GetPreferredInstance();
            return CurrentInstance;
        }

        // Actual worker for GetPreferredInstance()
        internal KSP _GetPreferredInstance()
        {
            // First check if we're part of a portable install
            // Note that this *does not* register in the registry.
            string path = KSP.PortableDir();

            if (path != null)
            {
                KSP portableInst = new KSP(path, "portable", User);
                if (portableInst.Valid)
                {
                    return portableInst;
                }
            }

            // If we only know of a single instance, return that.
            if (instances.Count == 1 && instances.First().Value.Valid)
            {
                return instances.First().Value;
            }

            // Return the autostart, if we can find it.
            // We check both null and "" as we can't write NULL to the registry, so we write an empty string instead
            // This is necessary so we can indicate that the user wants to reset the current AutoStartInstance without clearing the windows registry keys!
            if (!string.IsNullOrEmpty(AutoStartInstance)
                    && HasInstance(AutoStartInstance)
                    && instances[AutoStartInstance].Valid)
            {
                return instances[AutoStartInstance];
            }

            // If we know of no instances, try to find one.
            // Otherwise, we know of too many instances!
            // We don't know which one to pick, so we return null.
            return !instances.Any() ? FindAndRegisterDefaultInstance() : null;
        }

        /// <summary>
        /// Find and register a default instance by running
        /// game autodetection code.
        ///
        /// Returns the resulting KSP object if found.
        /// </summary>
        public KSP FindAndRegisterDefaultInstance()
        {
            if (instances.Any())
                throw new KSPManagerKraken("Attempted to scan for defaults with instances in registry");

            try
            {
                string gamedir = KSP.FindGameDir();
                KSP foundInst = new KSP(gamedir, "auto", User);
                return foundInst.Valid ? AddInstance(foundInst) : null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (NotKSPDirKraken)
            {
                return null;
            }
        }

        /// <summary>
        /// Adds a KSP instance to registry.
        /// Returns the resulting KSP object.
        /// </summary>
        public KSP AddInstance(KSP ksp_instance)
        {
            if (ksp_instance.Valid)
            {
                string name = ksp_instance.Name;
                instances.Add(name, ksp_instance);
                Win32Registry.SetRegistryToInstances(instances, AutoStartInstance);
            }
            else
            {
                throw new NotKSPDirKraken(ksp_instance.GameDir());
            }
            return ksp_instance;
        }

        /// <summary>
        /// Clones an existing KSP installation.
        /// </summary>
        /// <param name="existing_instance">The KSP instance to clone.</param>
        /// <param name="new_name">The name for the new instance.</param>
        /// <param name="new_path">The path where the new instance should be located.</param>
        public void CloneInstance (KSP existing_instance, string new_name, string new_path)
        {
            if (existing_instance.Valid)
            {
                KspVersion version = existing_instance.Version();

                CKAN.DLC.MakingHistoryDlcDetector dlcDetector = new DLC.MakingHistoryDlcDetector();
                bool DLC = dlcDetector.IsInstalled(existing_instance, out string identifier, out UnmanagedModuleVersion moduleVersion);
                string dlcVersion = moduleVersion.ToString();

                FakeInstance(new_name, new_path, version, DLC, dlcVersion);
            }
            else
            {
                NotKSPDirKraken kraken = new NotKSPDirKraken(existing_instance.GameDir());
                log.Error(kraken);
                throw kraken;
            }
        }

        /// <summary>
        /// Create a new fake KSP instance
        /// </summary>
        /// <param name="new_name">The name for the new instance.</param>
        /// <param name="new_path">The loaction of the new instance.</param>
        /// <param name="version">The version of the new instance.</param>
        /// <param name="DLC">Whether to fake the DLC too.</param>
        /// <param name="dlcVersion">The version of the DLC. Can be null if DLC == false.</param>
        public void FakeInstance(string new_name, string new_path, KspVersion version, bool DLC, string dlcVersion)
        {
            try
            {
                if (KSP.IsKspDir(new_path)) {
                    throw new BadInstallLocationKraken("There is already a KSP instance at this path. Delete the old one first.");
                }

                log.DebugFormat("Creating folder structure and text files at {0} for KSP version {1}", Path.GetFullPath(new_path), version.ToString());

                // Create a KSP root directory, containing a GameData folder, a buildID.txt and a readme.txt
                Directory.CreateDirectory(new_path);
                Directory.CreateDirectory(Path.Combine(new_path, "GameData"));
                File.WriteAllText(Path.Combine(new_path, "buildID.txt"), String.Format("build id = {0}", version.Build));
                File.WriteAllText(Path.Combine(new_path, "readme.txt"), String.Format("Version {0}", version.ToString()));

                // If a installed DLC should be simulated, we create the needed folder structure and the readme.txt
                if (DLC && version.CompareTo(new KspVersion(1, 4, 0)) >= 0)
                {
                    Directory.CreateDirectory(Path.Combine(new_path, "GameData", "SquadExpansion", "MakingHistory"));
                    File.WriteAllText(
                        Path.Combine(new_path, "GameData", "SquadExpansion", "MakingHistory", "readme.txt"),
                        String.Format("Version {0}", dlcVersion));
                }

                // Add the new instance to the registry
                KSP new_instance = new KSP(new_path, new_name, User);
                AddInstance(new_instance);
            }
            catch (Exception e)
            {
                log.Error(e);
                User.RaiseError(e.ToString());
            }
        }

        /// <summary>
        /// Given a string returns a unused valid instance name by postfixing the string
        /// </summary>
        /// <returns> A unused valid instance name.</returns>
        /// <param name="name">The name to use as a base.</param>
        /// <exception cref="CKAN.Kraken">Could not find a valid name.</exception>
        public string GetNextValidInstanceName(string name)
        {
            // Check if the current name is valid.
            if (InstanceNameIsValid(name))
            {
                return name;
            }

            // Try appending a number to the name.
            var validName = Enumerable.Repeat(name, 1000)
                .Select((s, i) => s + " (" + i + ")")
                .FirstOrDefault(InstanceNameIsValid);
            if (validName != null)
            {
                return validName;
            }

            // Check if a name with the current timestamp is valid.
            validName = name + " (" + DateTime.Now + ")";

            if (InstanceNameIsValid(validName))
            {
                return validName;
            }

            // Give up.
            throw new Kraken("Could not return a valid name for the new instance.");
        }

        /// <summary>
        /// Check if the instance name is valid.
        /// </summary>
        /// <returns><c>true</c>, if name is valid, <c>false</c> otherwise.</returns>
        /// <param name="name">Name to check.</param>
        private bool InstanceNameIsValid(string name)
        {
            // Discard null, empty strings and white space only strings.
            // Look for the current name in the list of loaded instances.
            return !String.IsNullOrWhiteSpace(name) && !HasInstance(name);
        }

        /// <summary>
        /// Removes the instance from the registry and saves.
        /// </summary>
        public void RemoveInstance(string name)
        {
            instances.Remove(name);
            Win32Registry.SetRegistryToInstances(instances, AutoStartInstance);
        }

        /// <summary>
        /// Renames an instance in the registry and saves.
        /// </summary>
        public void RenameInstance(string from, string to)
        {
            // TODO: What should we do if our target name already exists?
            KSP ksp = instances[from];
            instances.Remove(from);
            ksp.Name = to;
            instances.Add(to, ksp);
            Win32Registry.SetRegistryToInstances(instances, AutoStartInstance);
        }

        /// <summary>
        /// Sets the current instance.
        /// Throws an InvalidKSPInstanceKraken if not found.
        /// </summary>
        public void SetCurrentInstance(string name)
        {
            if (!HasInstance(name))
            {
                throw new InvalidKSPInstanceKraken(name);
            }
            else if (!instances[name].Valid)
            {
                throw new NotKSPDirKraken(instances[name].GameDir());
            }

            // Don't try to Dispose a null CurrentInstance.
            if (CurrentInstance != null)
            {
                // Dispose of the old registry manager, to release the registry.
                var manager = RegistryManager.Instance(CurrentInstance);
                if (manager != null)
                {
                    manager.Dispose();
                }
            }

            CurrentInstance = instances[name];
        }

        public void SetCurrentInstanceByPath(string path)
        {
            KSP ksp = new KSP(path, "custom", User);
            if (ksp.Valid)
            {
                CurrentInstance = ksp;
            }
            else
            {
                throw new NotKSPDirKraken(ksp.GameDir());
            }
        }

        /// <summary>
        /// Sets the autostart instance in the registry and saves it.
        /// </summary>
        public void SetAutoStart(string name)
        {
            if (!HasInstance(name))
            {
                throw new InvalidKSPInstanceKraken(name);
            }
            else if (!instances[name].Valid)
            {
                throw new NotKSPDirKraken(instances[name].GameDir());
            }
            AutoStartInstance = name;
        }

        public bool HasInstance(string name)
        {
            return instances.ContainsKey(name);
        }

        public void ClearAutoStart()
        {
            Win32Registry.AutoStartInstance = null;
        }

        public void LoadInstancesFromRegistry()
        {
            log.Info("Loading KSP instances from registry");

            instances.Clear();

            foreach (Tuple<string, string> instance in Win32Registry.GetInstances())
            {
                var name = instance.Item1;
                var path = instance.Item2;
                log.DebugFormat("Loading {0} from {1}", name, path);
                // Add unconditionally, sort out invalid instances downstream
                instances.Add(name, new KSP(path, name, User));
            }

            if (!Directory.Exists(Win32Registry.DownloadCacheDir))
            {
                Directory.CreateDirectory(Win32Registry.DownloadCacheDir);
            }
            string failReason;
            TrySetupCache(Win32Registry.DownloadCacheDir, out failReason);

            try
            {
                AutoStartInstance = Win32Registry.AutoStartInstance;
            }
            catch (InvalidKSPInstanceKraken e)
            {
                log.WarnFormat("Auto-start instance was invalid: {0}", e.Message);
                AutoStartInstance = null;
            }
        }

        /// <summary>
        /// Switch to using a download cache in a new location
        /// </summary>
        /// <param name="path">Location of folder for new cache</param>
        /// <returns>
        /// true if successful, false otherwise
        /// </returns>
        public bool TrySetupCache(string path, out string failureReason)
        {
            string origPath = Win32Registry.DownloadCacheDir;
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Win32Registry.DownloadCacheDir = "";
                    Cache = new NetModuleCache(this, Win32Registry.DownloadCacheDir);
                }
                else
                {
                    Cache = new NetModuleCache(this, path);
                    Win32Registry.DownloadCacheDir = path;
                }
                failureReason = null;
                return true;
            }
            catch (DirectoryNotFoundKraken)
            {
                failureReason = $"{path} does not exist";
                return false;
            }
            catch (PathErrorKraken ex)
            {
                failureReason = ex.Message;
                return false;
            }
            catch (IOException ex)
            {
                // MoveFrom failed, possibly full disk, so undo the change
                Win32Registry.DownloadCacheDir = origPath;
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Releases all resource used by the <see cref="CKAN.KSP"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="CKAN.KSP"/>. The <see cref="Dispose"/>
        /// method leaves the <see cref="CKAN.KSP"/> in an unusable state. After calling <see cref="Dispose"/>, you must
        /// release all references to the <see cref="CKAN.KSP"/> so the garbage collector can reclaim the memory that
        /// the <see cref="CKAN.KSP"/> was occupying.</remarks>
        public void Dispose()
        {
            if (Cache != null)
            {
                Cache.Dispose();
                Cache = null;
            }

            // Attempting to dispose of the related RegistryManager object here is a bad idea, it cause loads of failures
        }

    }

    public class KSPManagerKraken : Kraken
    {
        public KSPManagerKraken(string reason = null, Exception innerException = null)
            : base(reason, innerException)
        {
        }
    }

    public class InvalidKSPInstanceKraken : Exception
    {
        public string instance;

        public InvalidKSPInstanceKraken(string instance, string reason = null, Exception innerException = null)
            : base(reason, innerException)
        {
            this.instance = instance;
        }
    }
}
