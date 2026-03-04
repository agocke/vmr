// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.IO
{
    public static partial class FileSystemAclExtensions
    {
        public static void Create(this System.IO.DirectoryInfo directoryInfo, System.Security.AccessControl.DirectorySecurity directorySecurity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.IO.FileStream Create(this System.IO.FileInfo fileInfo, System.IO.FileMode mode, System.Security.AccessControl.FileSystemRights rights, System.IO.FileShare share, int bufferSize, System.IO.FileOptions options, System.Security.AccessControl.FileSecurity? fileSecurity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.IO.DirectoryInfo CreateDirectory(this System.Security.AccessControl.DirectorySecurity directorySecurity, string path) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.DirectorySecurity GetAccessControl(this System.IO.DirectoryInfo directoryInfo) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.DirectorySecurity GetAccessControl(this System.IO.DirectoryInfo directoryInfo, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.FileSecurity GetAccessControl(this System.IO.FileInfo fileInfo) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.FileSecurity GetAccessControl(this System.IO.FileInfo fileInfo, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.FileSecurity GetAccessControl(this System.IO.FileStream fileStream) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static void SetAccessControl(this System.IO.DirectoryInfo directoryInfo, System.Security.AccessControl.DirectorySecurity directorySecurity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static void SetAccessControl(this System.IO.FileInfo fileInfo, System.Security.AccessControl.FileSecurity fileSecurity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static void SetAccessControl(this System.IO.FileStream fileStream, System.Security.AccessControl.FileSecurity fileSecurity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
}
namespace System.Security.AccessControl
{
    public abstract partial class DirectoryObjectSecurity : System.Security.AccessControl.ObjectSecurity
    {
        protected DirectoryObjectSecurity() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected DirectoryObjectSecurity(System.Security.AccessControl.CommonSecurityDescriptor securityDescriptor) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual System.Security.AccessControl.AccessRule AccessRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void AddAccessRule(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void AddAuditRule(System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual System.Security.AccessControl.AuditRule AuditRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuthorizationRuleCollection GetAccessRules(bool includeExplicit, bool includeInherited, System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuthorizationRuleCollection GetAuditRules(bool includeExplicit, bool includeInherited, System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected override bool ModifyAccess(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AccessRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected override bool ModifyAudit(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AuditRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected bool RemoveAccessRule(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAccessRuleAll(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAccessRuleSpecific(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected bool RemoveAuditRule(System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAuditRuleAll(System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAuditRuleSpecific(System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void ResetAccessRule(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void SetAccessRule(System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void SetAuditRule(System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class DirectorySecurity : System.Security.AccessControl.FileSystemSecurity
    {
        public DirectorySecurity() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public DirectorySecurity(string name, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class FileSecurity : System.Security.AccessControl.FileSystemSecurity
    {
        public FileSecurity() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSecurity(string fileName, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class FileSystemAccessRule : System.Security.AccessControl.AccessRule
    {
        public FileSystemAccessRule(System.Security.Principal.IdentityReference identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAccessRule(System.Security.Principal.IdentityReference identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAccessRule(string identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAccessRule(string identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.FileSystemRights FileSystemRights { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public sealed partial class FileSystemAuditRule : System.Security.AccessControl.AuditRule
    {
        public FileSystemAuditRule(System.Security.Principal.IdentityReference identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAuditRule(System.Security.Principal.IdentityReference identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAuditRule(string identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public FileSystemAuditRule(string identity, System.Security.AccessControl.FileSystemRights fileSystemRights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.FileSystemRights FileSystemRights { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    [System.FlagsAttribute]
    public enum FileSystemRights
    {
        ListDirectory = 1,
        ReadData = 1,
        CreateFiles = 2,
        WriteData = 2,
        AppendData = 4,
        CreateDirectories = 4,
        ReadExtendedAttributes = 8,
        WriteExtendedAttributes = 16,
        ExecuteFile = 32,
        Traverse = 32,
        DeleteSubdirectoriesAndFiles = 64,
        ReadAttributes = 128,
        WriteAttributes = 256,
        Write = 278,
        Delete = 65536,
        ReadPermissions = 131072,
        Read = 131209,
        ReadAndExecute = 131241,
        Modify = 197055,
        ChangePermissions = 262144,
        TakeOwnership = 524288,
        Synchronize = 1048576,
        FullControl = 2032127,
    }
    public abstract partial class FileSystemSecurity : System.Security.AccessControl.NativeObjectSecurity
    {
        internal FileSystemSecurity() : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override System.Type AccessRightType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Type AccessRuleType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Type AuditRuleType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public sealed override System.Security.AccessControl.AccessRule AccessRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAccessRule(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAuditRule(System.Security.AccessControl.FileSystemAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public sealed override System.Security.AccessControl.AuditRule AuditRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAccessRule(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAccessRuleAll(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAccessRuleSpecific(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAuditRule(System.Security.AccessControl.FileSystemAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAuditRuleAll(System.Security.AccessControl.FileSystemAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAuditRuleSpecific(System.Security.AccessControl.FileSystemAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void ResetAccessRule(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAccessRule(System.Security.AccessControl.FileSystemAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAuditRule(System.Security.AccessControl.FileSystemAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
}
