$ErrorActionPreference='Stop'
function Get-PortableKeyPasswordBytes {
    param([Security.SecureString]$Password)
    $pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try {return ,([Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)))}
    finally {[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)}
}
function Get-PortableKeyMaterial {
    param([Security.SecureString]$Password,[byte[]]$Salt)
    if($Password.Length -lt 16){throw 'Use a password of at least 16 characters. Do not send it in chat.'}
    $bytes=Get-PortableKeyPasswordBytes $Password
    if($bytes.Length -lt 16){[Array]::Clear($bytes,0,$bytes.Length);throw 'Use a long password: at least 16 UTF-8 bytes. Do not send it in chat.'}
    try {
        $derive=[Security.Cryptography.Rfc2898DeriveBytes]::new($bytes,$Salt,310000,[Security.Cryptography.HashAlgorithmName]::SHA256)
        try {return ,($derive.GetBytes(64))}finally{$derive.Dispose()}
    }finally {[Array]::Clear($bytes,0,$bytes.Length)}
}
function Get-PortableKeyMac {
    param([hashtable]$Envelope,[byte[]]$Material)
    $mac=[Security.Cryptography.HMACSHA256]::new([byte[]]$Material[32..63])
    try {return ,($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes("ByPass-key-v1`n310000`n"+$Envelope.salt+"`n"+$Envelope.iv+"`n"+$Envelope.ciphertext)))}finally{$mac.Dispose()}
}
function Protect-PortableKey {
    param([byte[]]$Plain,[Security.SecureString]$Password)
    $random=[Security.Cryptography.RandomNumberGenerator]::Create();$salt=[byte[]]::new(16)
    try{$random.GetBytes($salt)}finally{$random.Dispose()}
    $material=Get-PortableKeyMaterial $Password $salt
    $aes=[Security.Cryptography.Aes]::Create();$aes.Key=[byte[]]$material[0..31];$aes.GenerateIV()
    try {
        $encryptor=$aes.CreateEncryptor()
        try{$ciphertext=$encryptor.TransformFinalBlock($Plain,0,$Plain.Length)}finally{$encryptor.Dispose()}
        $envelope=@{schema=1;kdf='PBKDF2-SHA256';iterations=310000;salt=[Convert]::ToBase64String($salt);iv=[Convert]::ToBase64String($aes.IV);ciphertext=[Convert]::ToBase64String($ciphertext)}
        $envelope.mac=[Convert]::ToBase64String((Get-PortableKeyMac $envelope $material))
        return ($envelope | ConvertTo-Json)
    }finally {$aes.Dispose();[Array]::Clear($material,0,$material.Length)}
}
function Unprotect-PortableKey {
    param([string]$Json,[Security.SecureString]$Password)
    if($Json.Length -gt 65536){throw 'Key backup is too large'}
    $envelope=$Json | ConvertFrom-Json -AsHashtable
    if($envelope.schema -ne 1 -or $envelope.kdf -ne 'PBKDF2-SHA256' -or $envelope.iterations -ne 310000){throw 'Unsupported key backup format'}
    $salt=[Convert]::FromBase64String($envelope.salt);$iv=[Convert]::FromBase64String($envelope.iv);$cipher=[Convert]::FromBase64String($envelope.ciphertext);$expected=[Convert]::FromBase64String($envelope.mac)
    if($salt.Length -ne 16 -or $iv.Length -ne 16 -or $expected.Length -ne 32 -or $cipher.Length -lt 16 -or $cipher.Length%16 -ne 0){throw 'Invalid key backup'}
    $material=Get-PortableKeyMaterial $Password $salt
    try {
        $actual=Get-PortableKeyMac $envelope $material;$difference=0
        for($index=0;$index -lt 32;$index++){$difference=$difference -bor ($actual[$index] -bxor $expected[$index])}
        if($difference -ne 0){throw 'Wrong password or modified key backup'}
        $aes=[Security.Cryptography.Aes]::Create();$aes.Key=[byte[]]$material[0..31];$aes.IV=$iv
        try{$decryptor=$aes.CreateDecryptor();try{return ,($decryptor.TransformFinalBlock($cipher,0,$cipher.Length))}finally{$decryptor.Dispose()}}finally{$aes.Dispose()}
    }finally{[Array]::Clear($material,0,$material.Length)}
}
function Set-ByPassPrivateDirectory {
    param([string]$Path)
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid)
    foreach($identity in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
