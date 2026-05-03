// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Security.AccessControl
{
    [System.FlagsAttribute]
    public enum AccessControlActions
    {
        None = 0,
        View = 1,
        Change = 2,
    }
    public enum AccessControlModification
    {
        Add = 0,
        Set = 1,
        Reset = 2,
        Remove = 3,
        RemoveAll = 4,
        RemoveSpecific = 5,
    }
    [System.FlagsAttribute]
    public enum AccessControlSections
    {
        None = 0,
        Audit = 1,
        Access = 2,
        Owner = 4,
        Group = 8,
        All = 15,
    }
    public enum AccessControlType
    {
        Allow = 0,
        Deny = 1,
    }
    public abstract partial class AccessRule : System.Security.AccessControl.AuthorizationRule
    {
        protected AccessRule(System.Security.Principal.IdentityReference identity, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AccessControlType AccessControlType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public partial class AccessRule<T> : System.Security.AccessControl.AccessRule where T : struct
    {
        public AccessRule(System.Security.Principal.IdentityReference identity, T rights, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AccessRule(System.Security.Principal.IdentityReference identity, T rights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AccessRule(string identity, T rights, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AccessRule(string identity, T rights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public T Rights { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public sealed partial class AceEnumerator : System.Collections.IEnumerator
    {
        internal AceEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.GenericAce Current { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        object System.Collections.IEnumerator.Current { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool MoveNext() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void Reset() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    [System.FlagsAttribute]
    public enum AceFlags : byte
    {
        None = (byte)0,
        ObjectInherit = (byte)1,
        ContainerInherit = (byte)2,
        NoPropagateInherit = (byte)4,
        InheritOnly = (byte)8,
        InheritanceFlags = (byte)15,
        Inherited = (byte)16,
        SuccessfulAccess = (byte)64,
        FailedAccess = (byte)128,
        AuditFlags = (byte)192,
    }
    public enum AceQualifier
    {
        AccessAllowed = 0,
        AccessDenied = 1,
        SystemAudit = 2,
        SystemAlarm = 3,
    }
    public enum AceType : byte
    {
        AccessAllowed = (byte)0,
        AccessDenied = (byte)1,
        SystemAudit = (byte)2,
        SystemAlarm = (byte)3,
        AccessAllowedCompound = (byte)4,
        AccessAllowedObject = (byte)5,
        AccessDeniedObject = (byte)6,
        SystemAuditObject = (byte)7,
        SystemAlarmObject = (byte)8,
        AccessAllowedCallback = (byte)9,
        AccessDeniedCallback = (byte)10,
        AccessAllowedCallbackObject = (byte)11,
        AccessDeniedCallbackObject = (byte)12,
        SystemAuditCallback = (byte)13,
        SystemAlarmCallback = (byte)14,
        SystemAuditCallbackObject = (byte)15,
        MaxDefinedAceType = (byte)16,
        SystemAlarmCallbackObject = (byte)16,
    }
    [System.FlagsAttribute]
    public enum AuditFlags
    {
        None = 0,
        Success = 1,
        Failure = 2,
    }
    public abstract partial class AuditRule : System.Security.AccessControl.AuthorizationRule
    {
        protected AuditRule(System.Security.Principal.IdentityReference identity, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags auditFlags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuditFlags AuditFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public partial class AuditRule<T> : System.Security.AccessControl.AuditRule where T : struct
    {
        public AuditRule(System.Security.Principal.IdentityReference identity, T rights, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AuditRule(System.Security.Principal.IdentityReference identity, T rights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AuditRule(string identity, T rights, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public AuditRule(string identity, T rights, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public T Rights { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public abstract partial class AuthorizationRule
    {
        protected internal AuthorizationRule(System.Security.Principal.IdentityReference identity, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected internal int AccessMask { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.Principal.IdentityReference IdentityReference { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.InheritanceFlags InheritanceFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsInherited { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.PropagationFlags PropagationFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public sealed partial class AuthorizationRuleCollection : System.Collections.ReadOnlyCollectionBase
    {
        public AuthorizationRuleCollection() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuthorizationRule? this[int index] { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public void AddRule(System.Security.AccessControl.AuthorizationRule? rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void CopyTo(System.Security.AccessControl.AuthorizationRule[] rules, int index) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class CommonAce : System.Security.AccessControl.QualifiedAce
    {
        public CommonAce(System.Security.AccessControl.AceFlags flags, System.Security.AccessControl.AceQualifier qualifier, int accessMask, System.Security.Principal.SecurityIdentifier sid, bool isCallback, byte[]? opaque) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static int MaxOpaqueLength(bool isCallback) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class CommonAcl : System.Security.AccessControl.GenericAcl
    {
        internal CommonAcl() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public sealed override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public sealed override int Count { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsCanonical { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsContainer { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsDS { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public sealed override System.Security.AccessControl.GenericAce this[int index] { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public sealed override byte Revision { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public sealed override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void Purge(System.Security.Principal.SecurityIdentifier sid) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveInheritedAces() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class CommonObjectSecurity : System.Security.AccessControl.ObjectSecurity
    {
        protected CommonObjectSecurity(bool isContainer) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void AddAccessRule(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void AddAuditRule(System.Security.AccessControl.AuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuthorizationRuleCollection GetAccessRules(bool includeExplicit, bool includeInherited, System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AuthorizationRuleCollection GetAuditRules(bool includeExplicit, bool includeInherited, System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected override bool ModifyAccess(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AccessRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected override bool ModifyAudit(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AuditRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected bool RemoveAccessRule(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAccessRuleAll(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAccessRuleSpecific(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected bool RemoveAuditRule(System.Security.AccessControl.AuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAuditRuleAll(System.Security.AccessControl.AuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void RemoveAuditRuleSpecific(System.Security.AccessControl.AuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void ResetAccessRule(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void SetAccessRule(System.Security.AccessControl.AccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void SetAuditRule(System.Security.AccessControl.AuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class CommonSecurityDescriptor : System.Security.AccessControl.GenericSecurityDescriptor
    {
        public CommonSecurityDescriptor(bool isContainer, bool isDS, byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public CommonSecurityDescriptor(bool isContainer, bool isDS, System.Security.AccessControl.ControlFlags flags, System.Security.Principal.SecurityIdentifier? owner, System.Security.Principal.SecurityIdentifier? group, System.Security.AccessControl.SystemAcl? systemAcl, System.Security.AccessControl.DiscretionaryAcl? discretionaryAcl) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public CommonSecurityDescriptor(bool isContainer, bool isDS, System.Security.AccessControl.RawSecurityDescriptor rawSecurityDescriptor) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public CommonSecurityDescriptor(bool isContainer, bool isDS, string sddlForm) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override System.Security.AccessControl.ControlFlags ControlFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.DiscretionaryAcl? DiscretionaryAcl { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.Principal.SecurityIdentifier? Group { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsContainer { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsDiscretionaryAclCanonical { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsDS { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsSystemAclCanonical { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.Principal.SecurityIdentifier? Owner { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.SystemAcl? SystemAcl { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public void AddDiscretionaryAcl(byte revision, int trusted) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddSystemAcl(byte revision, int trusted) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void PurgeAccessControl(System.Security.Principal.SecurityIdentifier sid) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void PurgeAudit(System.Security.Principal.SecurityIdentifier sid) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetDiscretionaryAclProtection(bool isProtected, bool preserveInheritance) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetSystemAclProtection(bool isProtected, bool preserveInheritance) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class CompoundAce : System.Security.AccessControl.KnownAce
    {
        public CompoundAce(System.Security.AccessControl.AceFlags flags, int accessMask, System.Security.AccessControl.CompoundAceType compoundAceType, System.Security.Principal.SecurityIdentifier sid) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.CompoundAceType CompoundAceType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public enum CompoundAceType
    {
        Impersonation = 1,
    }
    [System.FlagsAttribute]
    public enum ControlFlags
    {
        None = 0,
        OwnerDefaulted = 1,
        GroupDefaulted = 2,
        DiscretionaryAclPresent = 4,
        DiscretionaryAclDefaulted = 8,
        SystemAclPresent = 16,
        SystemAclDefaulted = 32,
        DiscretionaryAclUntrusted = 64,
        ServerSecurity = 128,
        DiscretionaryAclAutoInheritRequired = 256,
        SystemAclAutoInheritRequired = 512,
        DiscretionaryAclAutoInherited = 1024,
        SystemAclAutoInherited = 2048,
        DiscretionaryAclProtected = 4096,
        SystemAclProtected = 8192,
        RMControlValid = 16384,
        SelfRelative = 32768,
    }
    public sealed partial class CustomAce : System.Security.AccessControl.GenericAce
    {
        public static readonly int MaxOpaqueLength;
        public CustomAce(System.Security.AccessControl.AceType type, System.Security.AccessControl.AceFlags flags, byte[]? opaque) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public int OpaqueLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public byte[]? GetOpaque() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetOpaque(byte[]? opaque) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class DiscretionaryAcl : System.Security.AccessControl.CommonAcl
    {
        public DiscretionaryAcl(bool isContainer, bool isDS, byte revision, int capacity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public DiscretionaryAcl(bool isContainer, bool isDS, int capacity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public DiscretionaryAcl(bool isContainer, bool isDS, System.Security.AccessControl.RawAcl? rawAcl) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAccessSpecific(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAccessSpecific(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAccessSpecific(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAccess(System.Security.AccessControl.AccessControlType accessType, System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAccessRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class GenericAce
    {
        internal GenericAce() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AceFlags AceFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.AceType AceType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.AuditFlags AuditFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract int BinaryLength { get; }
        public System.Security.AccessControl.InheritanceFlags InheritanceFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsInherited { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.PropagationFlags PropagationFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.GenericAce Copy() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static System.Security.AccessControl.GenericAce CreateFromBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public sealed override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] object? o) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public abstract void GetBinaryForm(byte[] binaryForm, int offset);
        public sealed override int GetHashCode() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static bool operator ==(System.Security.AccessControl.GenericAce? left, System.Security.AccessControl.GenericAce? right) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static bool operator !=(System.Security.AccessControl.GenericAce? left, System.Security.AccessControl.GenericAce? right) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class GenericAcl : System.Collections.ICollection, System.Collections.IEnumerable
    {
        public static readonly byte AclRevision;
        public static readonly byte AclRevisionDS;
        public static readonly int MaxBinaryLength;
        protected GenericAcl() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public abstract int BinaryLength { get; }
        public abstract int Count { get; }
        public bool IsSynchronized { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract System.Security.AccessControl.GenericAce this[int index] { get; set; }
        public abstract byte Revision { get; }
        public virtual object SyncRoot { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public void CopyTo(System.Security.AccessControl.GenericAce[] array, int index) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public abstract void GetBinaryForm(byte[] binaryForm, int offset);
        public System.Security.AccessControl.AceEnumerator GetEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        void System.Collections.ICollection.CopyTo(System.Array array, int index) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class GenericSecurityDescriptor
    {
        internal GenericSecurityDescriptor() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract System.Security.AccessControl.ControlFlags ControlFlags { get; }
        public abstract System.Security.Principal.SecurityIdentifier? Group { get; set; }
        public abstract System.Security.Principal.SecurityIdentifier? Owner { get; set; }
        public static byte Revision { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public string GetSddlForm(System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static bool IsSddlConversionSupported() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    [System.FlagsAttribute]
    public enum InheritanceFlags
    {
        None = 0,
        ContainerInherit = 1,
        ObjectInherit = 2,
    }
    public abstract partial class KnownAce : System.Security.AccessControl.GenericAce
    {
        internal KnownAce() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public int AccessMask { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.Principal.SecurityIdentifier SecurityIdentifier { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public abstract partial class NativeObjectSecurity : System.Security.AccessControl.CommonObjectSecurity
    {
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, System.Runtime.InteropServices.SafeHandle? handle, System.Security.AccessControl.AccessControlSections includeSections) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, System.Runtime.InteropServices.SafeHandle? handle, System.Security.AccessControl.AccessControlSections includeSections, System.Security.AccessControl.NativeObjectSecurity.ExceptionFromErrorCode? exceptionFromErrorCode, object? exceptionContext) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, System.Security.AccessControl.NativeObjectSecurity.ExceptionFromErrorCode? exceptionFromErrorCode, object? exceptionContext) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, string? name, System.Security.AccessControl.AccessControlSections includeSections) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected NativeObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, string? name, System.Security.AccessControl.AccessControlSections includeSections, System.Security.AccessControl.NativeObjectSecurity.ExceptionFromErrorCode? exceptionFromErrorCode, object? exceptionContext) : base (default(bool)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected sealed override void Persist(System.Runtime.InteropServices.SafeHandle handle, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void Persist(System.Runtime.InteropServices.SafeHandle handle, System.Security.AccessControl.AccessControlSections includeSections, object? exceptionContext) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected sealed override void Persist(string name, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void Persist(string name, System.Security.AccessControl.AccessControlSections includeSections, object? exceptionContext) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected internal delegate System.Exception? ExceptionFromErrorCode(int errorCode, string? name, System.Runtime.InteropServices.SafeHandle? handle, object? context);
    }
    public abstract partial class ObjectAccessRule : System.Security.AccessControl.AccessRule
    {
        protected ObjectAccessRule(System.Security.Principal.IdentityReference identity, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Guid objectType, System.Guid inheritedObjectType, System.Security.AccessControl.AccessControlType type) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AccessControlType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Guid InheritedObjectType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.ObjectAceFlags ObjectFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Guid ObjectType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public sealed partial class ObjectAce : System.Security.AccessControl.QualifiedAce
    {
        public ObjectAce(System.Security.AccessControl.AceFlags aceFlags, System.Security.AccessControl.AceQualifier qualifier, int accessMask, System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAceFlags flags, System.Guid type, System.Guid inheritedType, bool isCallback, byte[]? opaque) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Guid InheritedObjectAceType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.ObjectAceFlags ObjectAceFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Guid ObjectAceType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static int MaxOpaqueLength(bool isCallback) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    [System.FlagsAttribute]
    public enum ObjectAceFlags
    {
        None = 0,
        ObjectAceTypePresent = 1,
        InheritedObjectAceTypePresent = 2,
    }
    public abstract partial class ObjectAuditRule : System.Security.AccessControl.AuditRule
    {
        protected ObjectAuditRule(System.Security.Principal.IdentityReference identity, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Guid objectType, System.Guid inheritedObjectType, System.Security.AccessControl.AuditFlags auditFlags) : base (default(System.Security.Principal.IdentityReference), default(int), default(bool), default(System.Security.AccessControl.InheritanceFlags), default(System.Security.AccessControl.PropagationFlags), default(System.Security.AccessControl.AuditFlags)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Guid InheritedObjectType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.ObjectAceFlags ObjectFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Guid ObjectType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
    }
    public abstract partial class ObjectSecurity
    {
        protected ObjectSecurity() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(bool isContainer, bool isDS) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(System.Security.AccessControl.CommonSecurityDescriptor securityDescriptor) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public abstract System.Type AccessRightType { get; }
        protected bool AccessRulesModified { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract System.Type AccessRuleType { get; }
        public bool AreAccessRulesCanonical { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool AreAccessRulesProtected { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool AreAuditRulesCanonical { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool AreAuditRulesProtected { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        protected bool AuditRulesModified { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract System.Type AuditRuleType { get; }
        protected bool GroupModified { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        protected bool IsContainer { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        protected bool IsDS { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        protected bool OwnerModified { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        protected System.Security.AccessControl.CommonSecurityDescriptor SecurityDescriptor { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public abstract System.Security.AccessControl.AccessRule AccessRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type);
        public abstract System.Security.AccessControl.AuditRule AuditRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags);
        public System.Security.Principal.IdentityReference? GetGroup(System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.Principal.IdentityReference? GetOwner(System.Type targetType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public byte[] GetSecurityDescriptorBinaryForm() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public string GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public static bool IsSddlConversionSupported() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected abstract bool ModifyAccess(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AccessRule rule, out bool modified);
        public virtual bool ModifyAccessRule(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AccessRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected abstract bool ModifyAudit(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AuditRule rule, out bool modified);
        public virtual bool ModifyAuditRule(System.Security.AccessControl.AccessControlModification modification, System.Security.AccessControl.AuditRule rule, out bool modified) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected virtual void Persist(bool enableOwnershipPrivilege, string name, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected virtual void Persist(System.Runtime.InteropServices.SafeHandle handle, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected virtual void Persist(string name, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void PurgeAccessRules(System.Security.Principal.IdentityReference identity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void PurgeAuditRules(System.Security.Principal.IdentityReference identity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void ReadLock() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void ReadUnlock() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAccessRuleProtection(bool isProtected, bool preserveInheritance) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAuditRuleProtection(bool isProtected, bool preserveInheritance) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetGroup(System.Security.Principal.IdentityReference identity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetOwner(System.Security.Principal.IdentityReference identity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetSecurityDescriptorBinaryForm(byte[] binaryForm) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetSecurityDescriptorBinaryForm(byte[] binaryForm, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetSecurityDescriptorSddlForm(string sddlForm) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetSecurityDescriptorSddlForm(string sddlForm, System.Security.AccessControl.AccessControlSections includeSections) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void WriteLock() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected void WriteUnlock() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class ObjectSecurity<T> : System.Security.AccessControl.NativeObjectSecurity where T : struct
    {
        protected ObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType) : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, System.Runtime.InteropServices.SafeHandle? safeHandle, System.Security.AccessControl.AccessControlSections includeSections) : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, System.Runtime.InteropServices.SafeHandle? safeHandle, System.Security.AccessControl.AccessControlSections includeSections, System.Security.AccessControl.NativeObjectSecurity.ExceptionFromErrorCode? exceptionFromErrorCode, object? exceptionContext) : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, string? name, System.Security.AccessControl.AccessControlSections includeSections) : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected ObjectSecurity(bool isContainer, System.Security.AccessControl.ResourceType resourceType, string? name, System.Security.AccessControl.AccessControlSections includeSections, System.Security.AccessControl.NativeObjectSecurity.ExceptionFromErrorCode? exceptionFromErrorCode, object? exceptionContext) : base (default(bool), default(System.Security.AccessControl.ResourceType)) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override System.Type AccessRightType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Type AccessRuleType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Type AuditRuleType { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.AccessControl.AccessRule AccessRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AccessControlType type) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void AddAccessRule(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void AddAuditRule(System.Security.AccessControl.AuditRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override System.Security.AccessControl.AuditRule AuditRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.AuditFlags flags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected internal void Persist(System.Runtime.InteropServices.SafeHandle handle) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        protected internal void Persist(string name) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual bool RemoveAccessRule(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void RemoveAccessRuleAll(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void RemoveAccessRuleSpecific(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual bool RemoveAuditRule(System.Security.AccessControl.AuditRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void RemoveAuditRuleAll(System.Security.AccessControl.AuditRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void RemoveAuditRuleSpecific(System.Security.AccessControl.AuditRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void ResetAccessRule(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void SetAccessRule(System.Security.AccessControl.AccessRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual void SetAuditRule(System.Security.AccessControl.AuditRule<T> rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class PrivilegeNotHeldException : System.UnauthorizedAccessException, System.Runtime.Serialization.ISerializable
    {
        public PrivilegeNotHeldException() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public PrivilegeNotHeldException(string? privilege) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public PrivilegeNotHeldException(string? privilege, System.Exception? inner) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public string? PrivilegeName { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        [System.ObsoleteAttribute("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    [System.FlagsAttribute]
    public enum PropagationFlags
    {
        None = 0,
        NoPropagateInherit = 1,
        InheritOnly = 2,
    }
    public abstract partial class QualifiedAce : System.Security.AccessControl.KnownAce
    {
        internal QualifiedAce() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.AccessControl.AceQualifier AceQualifier { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsCallback { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public int OpaqueLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public byte[]? GetOpaque() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetOpaque(byte[]? opaque) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class RawAcl : System.Security.AccessControl.GenericAcl
    {
        public RawAcl(byte revision, int capacity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public RawAcl(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override int BinaryLength { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override int Count { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.AccessControl.GenericAce this[int index] { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override byte Revision { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override void GetBinaryForm(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void InsertAce(int index, System.Security.AccessControl.GenericAce ace) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAce(int index) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public sealed partial class RawSecurityDescriptor : System.Security.AccessControl.GenericSecurityDescriptor
    {
        public RawSecurityDescriptor(byte[] binaryForm, int offset) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public RawSecurityDescriptor(System.Security.AccessControl.ControlFlags flags, System.Security.Principal.SecurityIdentifier? owner, System.Security.Principal.SecurityIdentifier? group, System.Security.AccessControl.RawAcl? systemAcl, System.Security.AccessControl.RawAcl? discretionaryAcl) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public RawSecurityDescriptor(string sddlForm) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public override System.Security.AccessControl.ControlFlags ControlFlags { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.RawAcl? DiscretionaryAcl { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.Principal.SecurityIdentifier? Group { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public override System.Security.Principal.SecurityIdentifier? Owner { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public byte ResourceManagerControl { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public System.Security.AccessControl.RawAcl? SystemAcl { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public void SetFlags(System.Security.AccessControl.ControlFlags flags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public enum ResourceType
    {
        Unknown = 0,
        FileObject = 1,
        Service = 2,
        Printer = 3,
        RegistryKey = 4,
        LMShare = 5,
        KernelObject = 6,
        WindowObject = 7,
        DSObject = 8,
        DSObjectAll = 9,
        ProviderDefined = 10,
        WmiGuidObject = 11,
        RegistryWow6432Key = 12,
    }
    [System.FlagsAttribute]
    public enum SecurityInfos
    {
        Owner = 1,
        Group = 2,
        DiscretionaryAcl = 4,
        SystemAcl = 8,
    }
    public sealed partial class SystemAcl : System.Security.AccessControl.CommonAcl
    {
        public SystemAcl(bool isContainer, bool isDS, byte revision, int capacity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public SystemAcl(bool isContainer, bool isDS, int capacity) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public SystemAcl(bool isContainer, bool isDS, System.Security.AccessControl.RawAcl rawAcl) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAudit(System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public bool RemoveAudit(System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAuditSpecific(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAuditSpecific(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveAuditSpecific(System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAudit(System.Security.AccessControl.AuditFlags auditFlags, System.Security.Principal.SecurityIdentifier sid, int accessMask, System.Security.AccessControl.InheritanceFlags inheritanceFlags, System.Security.AccessControl.PropagationFlags propagationFlags, System.Security.AccessControl.ObjectAceFlags objectFlags, System.Guid objectType, System.Guid inheritedObjectType) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void SetAudit(System.Security.Principal.SecurityIdentifier sid, System.Security.AccessControl.ObjectAuditRule rule) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
}

namespace System.Security.Policy
{
    public sealed partial class Evidence : System.Collections.ICollection, System.Collections.IEnumerable
    {
        public Evidence() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        [System.ObsoleteAttribute("This constructor is obsolete. Use the constructor which accepts arrays of EvidenceBase instead.")]
        public Evidence(object[] hostEvidence, object[] assemblyEvidence) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public Evidence(System.Security.Policy.Evidence evidence) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public Evidence(System.Security.Policy.EvidenceBase[] hostEvidence, System.Security.Policy.EvidenceBase[] assemblyEvidence) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        [System.ObsoleteAttribute("Evidence should not be treated as an ICollection. Use GetHostEnumerator and GetAssemblyEnumerator to iterate over the evidence to collect a count.")]
        public int Count { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsReadOnly { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool IsSynchronized { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public bool Locked { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } set { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        public object SyncRoot { get { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  } }
        [System.ObsoleteAttribute("Evidence.AddAssembly has been deprecated. Use AddAssemblyEvidence instead.")]
        public void AddAssembly(object id) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddAssemblyEvidence<T>(T evidence) where T : System.Security.Policy.EvidenceBase { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        [System.ObsoleteAttribute("Evidence.AddHost has been deprecated. Use AddHostEvidence instead.")]
        public void AddHost(object id) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void AddHostEvidence<T>(T evidence) where T : System.Security.Policy.EvidenceBase { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void Clear() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Security.Policy.Evidence? Clone() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        [System.ObsoleteAttribute("Evidence should not be treated as an ICollection. Use the GetHostEnumerator and GetAssemblyEnumerator methods rather than using CopyTo.")]
        public void CopyTo(System.Array array, int index) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Collections.IEnumerator GetAssemblyEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public T? GetAssemblyEvidence<T>() where T : System.Security.Policy.EvidenceBase { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        [System.ObsoleteAttribute("GetEnumerator is obsolete. Use GetAssemblyEnumerator and GetHostEnumerator instead.")]
        public System.Collections.IEnumerator GetEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public System.Collections.IEnumerator GetHostEnumerator() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public T? GetHostEvidence<T>() where T : System.Security.Policy.EvidenceBase { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void Merge(System.Security.Policy.Evidence evidence) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public void RemoveType(System.Type t) { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
    public abstract partial class EvidenceBase
    {
        protected EvidenceBase() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
        public virtual System.Security.Policy.EvidenceBase? Clone() { throw new System.PlatformNotSupportedException(System.SR.PlatformNotSupported_AccessControl);  }
    }
}
