[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MumuRoot,
    [string]$Output,
    [string]$AdbSerial,
    [switch]$DryRun,
    [switch]$ConfirmRisk,
    [switch]$ReuseMumu,
    [switch]$RestartMumu,
    [ValidateRange(30, 1800)]
    [int]$GuestTimeoutSeconds = 45,
    [switch]$EnableProducerObserver,
    [switch]$EnableResolveObserver,
    [switch]$EnableSerializedIdentityObserver,
    [switch]$EnableIdentityCallerPatch,
    [switch]$EnablePPtrObserver,
    [ValidateSet('both', 'entry-only', 'return-only')]
    [string]$PPtrScopeMode = 'return-only',
    [string]$PPtrCallsiteRva,
    [string]$PPtrCallsiteReturnRva,
    [string]$PPtrCallsiteReturnFirstInstruction,
    [string]$PPtrCallsiteKeyStackOffset,
    [string]$PPtrCallsiteOutputStackOffset,
    [string]$PPtrCallsiteLookupCallerRva,
    [string]$PPtrCallsiteLookupBranchInstruction,
    [string]$PPtrCallsiteMaxRecords = '4096',
    [switch]$EnablePPtrGuestPatch,
    [switch]$EnableObjectLookupObserver,
    [switch]$EnableSerializedObjectLoadBoundaryObserver,
    [switch]$EnableSerializedObjectConsumerObserver,
    [switch]$EnableSerializedFieldReadObserver,
    [switch]$EnableObjectResolveCandidateObserver,
    [switch]$EnableHsr45OpaqueCallsiteObserver,
    [ValidateRange(0, 15)]
    [int]$OpaqueCallsiteProbeMask = 0,
    [switch]$AllowUnsafeOpaqueProbeCombination,
    [switch]$EnableContextObserver,
    [switch]$EnableContextObserverAll,
    [switch]$EnableTransferCallsiteEvidence,
    [switch]$EnableMaterialFieldTransferObserver,
    [switch]$EnableMaterialMShaderCallsiteObserver,
      [switch]$EnableGuestIdentityPatches,
    [switch]$EnableGuestIdentityRawPatch,
    [string]$SerializedIdentityRva = '0x23befb0',
    [string]$SerializedIdentityFirstInstruction = '0xd10283ff',
    [switch]$EnableNativeTypeTree,
    [switch]$AllowTranslatedHostHook,
    [string]$ResolveRuntimeFirstInstruction,
    [string]$ResolveRva = '0x23a83b8',
    [string]$ResolveFirstInstruction = '0xff8301d1',
    # Native TypeTree uses +0xA8, while the normal Material Transfer callback
    # observed by the historical successful runs is at +0xA0.
    [string]$ObserveVtableOffset = '0xA0',
    [string]$GuestClassIds = '21',
    # Runtime evidence is a bounded probe, not a passive overnight collector.
    # The user can still override these values for a deliberately longer run.
    [int]$GuestEventSeconds = 8,
    [int]$GuestEventMaxSeconds = 20,
    [int]$GuestEventIdleSeconds = 3,
    [ValidateRange(30, 1800)]
    [int]$GuestTotalTimeoutSeconds = 90,
    [ValidateRange(0, 4096)]
    [int]$GuestSnapshotBytes = 0,
    [string]$GuestSnapshotClassIds,
    [int]$LoadBoundaryMaxRecords = 16384,
    [int]$ConsumerMaxRecords = 8192,
    [int]$ResolveCandidateMaxCalls = 262144,
    [switch]$EnableSerializedFieldCallerObserver,
    [string]$SerializedFieldCallerRva,
    [string]$SerializedFieldCallerFirstInstruction,
    [string]$SerializedFieldCallerTargetArchitecture = 'arm64-guest',
    [int]$SerializedFieldReadMaxRecords = 4096,
    [string]$SerializedFieldReadNames,
    [ValidateRange(4096, 65536)]
    [int]$ObjectLookupMaxRecords = 16384
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$profile = Join-Path $repoRoot 'profiles\HSR-4.5-MUMU.json'
$dotnet = Join-Path $repoRoot '..\dotnet\8\dotnet.exe'
$tool = Join-Path $repoRoot 'src\MyHookTool\bin\Release\net8.0\my-hook-tool.dll'

if (-not $ConfirmRisk) {
    throw '这是 HSR 4.5 Material/Shader native TypeTree 实验，可能导致客体崩溃。需要显式传入 -ConfirmRisk。'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "找不到 .NET 8 运行时：$dotnet"
}
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
    throw "找不到 .NET 8 Release 工具，请先构建：$tool"
}
if (-not (Test-Path -LiteralPath $MumuRoot -PathType Container)) {
    throw "找不到 MuMu 根目录：$MumuRoot"
}
$classIds = @($GuestClassIds -split ',' | ForEach-Object {
    $value = $_.Trim()
    if ($value -notmatch '^[0-9]+$') {
        throw "GuestClassIds 包含无效 ClassID：$value"
    }
    [int]$value
}) | Select-Object -Unique
if ($classIds.Count -eq 0) {
    throw 'GuestClassIds 不能为空。'
}
$GuestClassIds = $classIds -join ','
if ($GuestEventSeconds -le 0) {
    throw 'GuestEventSeconds 必须是正整数。'
}
if ($GuestEventMaxSeconds -lt $GuestEventSeconds) {
    throw 'GuestEventMaxSeconds 必须不小于 GuestEventSeconds。'
}
if ($GuestEventIdleSeconds -lt 0) {
    throw 'GuestEventIdleSeconds 必须是非负整数。'
}
if ($LoadBoundaryMaxRecords -lt 1 -or $LoadBoundaryMaxRecords -gt 65536) {
    throw 'LoadBoundaryMaxRecords 必须在 1 到 65536 之间。'
}
if ($ConsumerMaxRecords -lt 1 -or $ConsumerMaxRecords -gt 16384) {
    throw 'ConsumerMaxRecords 必须在 1 到 16384 之间。'
}
if ($ResolveCandidateMaxCalls -lt 1 -or $ResolveCandidateMaxCalls -gt 1000000) {
    throw 'ResolveCandidateMaxCalls 必须在 1 到 1000000 之间。'
}
if ($EnableSerializedFieldCallerObserver) {
    if ([string]::IsNullOrWhiteSpace($SerializedFieldCallerRva) -or
        [string]::IsNullOrWhiteSpace($SerializedFieldCallerFirstInstruction)) {
        throw '启用 SerializedFieldCallerObserver 时必须同时提供 SerializedFieldCallerRva 和 SerializedFieldCallerFirstInstruction。'
    }
    if ($SerializedFieldCallerTargetArchitecture -notin @('arm64-guest', 'host-translated')) {
        throw 'SerializedFieldCallerTargetArchitecture 只能是 arm64-guest 或 host-translated。'
    }
}
if ($EnableHsr45OpaqueCallsiteObserver -and
    -not $EnableObjectResolveCandidateObserver) {
    throw 'HSR 4.5 opaque-callsite observer requires the typed resolver observer; PPtr scope is optional for direct callsite-to-return evidence.'
}
if ($EnableHsr45OpaqueCallsiteObserver -and $OpaqueCallsiteProbeMask -eq 0) {
    throw '启用 HSR 4.5 opaque-callsite observer 时必须显式选择非零 OpaqueCallsiteProbeMask。'
}
if ($EnableHsr45OpaqueCallsiteObserver -and -not $AllowUnsafeOpaqueProbeCombination) {
    $probeBits = 0
    for ($bit = 0; $bit -lt 4; $bit++) {
        if (($OpaqueCallsiteProbeMask -band (1 -shl $bit)) -ne 0) {
            $probeBits++
        }
    }
    if ($probeBits -gt 1) {
        throw 'HSR 4.5 opaque probes must be isolated: only one bit may be selected. Multi-probe combinations have guest SIGSEGV evidence under MuMu/Houdini. Use -AllowUnsafeOpaqueProbeCombination only for an explicitly controlled crash-isolation experiment.'
    }
}
if ($SerializedFieldReadMaxRecords -le 0) {
    throw 'SerializedFieldReadMaxRecords 必须是正整数。'
}
$adb = Join-Path $MumuRoot 'nx_main\adb.exe'
if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) {
    throw "找不到 MuMu ADB：$adb"
}
$connected = @(& $adb devices | Where-Object { $_ -match '^\S+\s+device$' } |
    ForEach-Object { ($_ -split '\s+')[0] })
$mumuSerial = $null
$manager = Join-Path $MumuRoot 'nx_main\mumumanager.exe'
if (Test-Path -LiteralPath $manager -PathType Leaf) {
    try {
        $info = (& $manager info --vmindex 0 | ConvertFrom-Json)
        $vm = $info.'0'
        if ($vm.adb_host_ip -and $vm.adb_port) {
            $mumuSerial = "$($vm.adb_host_ip):$($vm.adb_port)"
        }
    } catch {
        $mumuSerial = $null
    }
}
if ([string]::IsNullOrWhiteSpace($AdbSerial) -and $connected.Count -gt 1) {
    $bootIds = @($connected | ForEach-Object {
        (& $adb -s $_ shell cat /proc/sys/kernel/random/boot_id 2>$null).Trim()
    } | Where-Object { $_ }) | Select-Object -Unique
    if ($bootIds.Count -eq 1) {
        $AdbSerial = if ($mumuSerial -and $connected -contains $mumuSerial) {
            $mumuSerial
        } else {
            $connected[0]
        }
    }
}
if ([string]::IsNullOrWhiteSpace($AdbSerial)) {
    if ($connected.Count -gt 1) {
        throw "检测到多个 ADB 设备，请显式传入 -AdbSerial：$($connected -join ', ')"
    }
    if ($connected.Count -eq 1) {
        $AdbSerial = $connected[0]
    }
} elseif ($connected -notcontains $AdbSerial) {
    throw "指定的 ADB 设备不在线：$AdbSerial"
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot 'artifacts\HookSessions\native-typetree-hsr45'
}

if ($ReuseMumu -and $RestartMumu) {
    throw 'ReuseMumu 与 RestartMumu 不能同时使用。'
}

# Repeated experiments should not cold-boot the whole emulator when a healthy
# MuMu host and ADB device are already available. Fall back to the original
# cold-start path when either prerequisite is missing.
$reuseMumuEffective = $ReuseMumu.IsPresent
if (-not $ReuseMumu.IsPresent -and -not $RestartMumu.IsPresent) {
    $hostRunning = @(Get-Process -Name 'MuMuNxMain' -ErrorAction SilentlyContinue).Count -gt 0
    $adbReady = $false
    if ($hostRunning -and -not [string]::IsNullOrWhiteSpace($AdbSerial)) {
        $state = (& $adb -s $AdbSerial get-state 2>$null | Out-String).Trim()
        $adbReady = $state -eq 'device'
    }
    $reuseMumuEffective = $hostRunning -and $adbReady
}

& $dotnet $tool profile-check $profile
if ($LASTEXITCODE -ne 0) {
    throw 'HSR 4.5 Profile 自检失败，已停止实验。'
}
if ($DryRun) {
    Write-Output "Selected ADB serial: $AdbSerial"
    Write-Output "Guest ClassIDs: $GuestClassIds"
    Write-Output "HSR PPtr scope observer: $(if ($EnablePPtrObserver) { "enabled ($PPtrScopeMode)" } else { 'disabled' })"
    Write-Output "HSR opaque callsite probe mask: $OpaqueCallsiteProbeMask (1=input, 2=producer-return, 4=consumer-argument, 8=resolver-arguments)"
    Write-Output "Guest identity raw-patch: $(if ($EnableGuestIdentityRawPatch) { 'enabled' } else { 'disabled' }); native TypeTree: $(if ($EnableNativeTypeTree) { 'enabled' } else { 'disabled' })"
    Write-Output "Translated host hook: $(if ($AllowTranslatedHostHook) { 'enabled' } else { 'disabled' })"
    Write-Output "Transfer-context observer: $(if ($EnableContextObserver -or $EnableContextObserverAll) { 'enabled' } else { 'disabled' }); all observed types: $(if ($EnableContextObserverAll) { 'enabled' } else { 'disabled' })"
    Write-Output "Material m_Shader callsite observer: $(if ($EnableMaterialMShaderCallsiteObserver) { 'enabled' } else { 'disabled' })"
    Write-Output "MuMu VM reuse: $(if ($reuseMumuEffective) { 'enabled; only the HSR Android process will be restarted under LD_PRELOAD' } else { 'disabled; the MuMu VM will be restarted' })"
Write-Output "Wait for Unity runtime: up to $GuestTimeoutSeconds seconds after HSR process start"
    Write-Output "Guest event window: $GuestEventSeconds-$GuestEventMaxSeconds seconds; idle=$GuestEventIdleSeconds seconds"
    Write-Output "Guest total timeout: $GuestTotalTimeoutSeconds seconds; auto-enter is profile-driven"
    Write-Output "Serialized object observer caps: load-boundary=$LoadBoundaryMaxRecords; consumer=$ConsumerMaxRecords; resolve-candidate calls=$ResolveCandidateMaxCalls (records restricted to typed Material/Shader candidates; exact PPtr output-slot dataflow is attached when observed)"
    Write-Output 'Dry run completed; MuMu was not started and no native ABI was called.'
    exit 0
}

$arguments = @(
    'mumu', '--profile', $profile, '--mumu-root', $MumuRoot,
    '--output', $Output, '--launch-package', 'com.miHoYo.hkrpg',
    '--guest-timeout-seconds', $GuestTimeoutSeconds.ToString(),
    '--guest-class-ids', $GuestClassIds,
    '--vtable-probe', '0', '--observe-transfers', '1',
    '--observe-vtable-offset', $ObserveVtableOffset,
    '--observe-vtable-offsets', $ObserveVtableOffset,
    '--serialized-identity-observer', $(if ($EnableSerializedIdentityObserver) { '1' } else { '0' }),
    '--serialized-identity-rva', $SerializedIdentityRva,
    '--serialized-identity-first-instruction', $SerializedIdentityFirstInstruction,
    '--serialized-identity-guest-patch', $(if ($EnableGuestIdentityRawPatch) { '1' } else { '0' }),
    '--serialized-identity-caller-patch', $(if ($EnableIdentityCallerPatch) { '1' } else { '0' }),
    '--serialized-pptr-observer', $(if ($EnablePPtrObserver) { '1' } else { '0' }),
    '--serialized-object-resolve-candidate-observer', $(if ($EnableObjectResolveCandidateObserver) { '1' } else { '0' }),
    '--hsr45-opaque-callsite-observer', $(if ($EnableHsr45OpaqueCallsiteObserver) { '1' } else { '0' }),
    '--hsr45-opaque-callsite-probe-mask', $OpaqueCallsiteProbeMask.ToString(),
    '--serialized-object-load-boundary-observer', $(if ($EnableSerializedObjectLoadBoundaryObserver) { '1' } else { '0' }),
    '--serialized-object-load-boundary-rva', '0x23ab91c',
    '--serialized-object-load-boundary-first-instruction', '0xd10403ff',
    '--serialized-object-load-boundary-target-architecture', 'arm64-guest',
    '--serialized-object-consumer-observer', $(if ($EnableSerializedObjectConsumerObserver) { '1' } else { '0' }),
    '--serialized-object-consumer-rva', '0x23b9774',
    '--serialized-object-consumer-first-instruction', '0xa9ba7bfd',
    '--serialized-object-consumer-target-architecture', 'arm64-guest',
    '--serialized-object-consumer-caller-rva', '0x23ac580',
    '--context-observer', $(if ($EnableContextObserver -or $EnableContextObserverAll) { '1' } else { '0' }),
    '--capture-transfer-callers', $(if ($EnableTransferCallsiteEvidence) { '1' } else { '0' }),
    '--guest-event-seconds', $GuestEventSeconds.ToString(),
    '--guest-event-max-seconds', $GuestEventMaxSeconds.ToString(),
    '--guest-event-idle-seconds', $GuestEventIdleSeconds.ToString(),
    '--guest-total-timeout-seconds', $GuestTotalTimeoutSeconds.ToString()
)
if ($GuestSnapshotBytes -gt 0) {
    $arguments += @('--guest-snapshot-bytes', $GuestSnapshotBytes.ToString())
    $arguments += @('--guest-snapshot-class-ids', $(if ([string]::IsNullOrWhiteSpace($GuestSnapshotClassIds)) {
        $GuestClassIds
    } else {
        $GuestSnapshotClassIds
    }))
}
if ($reuseMumuEffective) {
    $arguments += '--reuse-mumu'
}
if (-not $AllowTranslatedHostHook) {
    $arguments += @('--serialized-identity-target-architecture', 'arm64-guest')
} else {
    $arguments += @('--serialized-identity-target-architecture', 'host-translated')
}
  if ($EnableNativeTypeTree) {
    $arguments += @(
        '--enable-native-transfer', '1',
        '--unsafe-experiment', 'I_UNDERSTAND_UNITY_CRASH_RISK',
        '--native-layout', '1',
        '--native-on-transfer', '1'
    )
  } else {
      $arguments += @(
          '--enable-native-transfer', '0',
          '--native-layout', '0',
          '--native-on-transfer', '0',
          '--unsafe-experiment', $(if ($EnableGuestIdentityPatches) {
              'I_UNDERSTAND_UNITY_CRASH_RISK'
          } else { '' })
      )
  }
if ($EnableProducerObserver) {
    $arguments += @(
        '--serialized-identity-producer-observer', '1',
        '--serialized-identity-producer-rva', '0x23a87a0',
        '--serialized-identity-producer-first-instruction', '0xf81a0ffe'
    )
    if (-not $AllowTranslatedHostHook) {
        $arguments += @('--serialized-identity-producer-target-architecture', 'arm64-guest')
    } else {
        $arguments += @('--serialized-identity-producer-target-architecture', 'host-translated')
    }
}
if ($EnableResolveObserver) {
    $arguments += @(
        '--serialized-identity-resolve-observer', '1',
        '--serialized-identity-resolve-rva', $ResolveRva,
        '--serialized-identity-resolve-first-instruction', $ResolveFirstInstruction
    )
    if (-not $AllowTranslatedHostHook) {
        $arguments += @('--serialized-identity-resolve-target-architecture', 'arm64-guest')
    } else {
        $arguments += @('--serialized-identity-resolve-target-architecture', 'host-translated')
    }
    if (-not [string]::IsNullOrWhiteSpace($ResolveRuntimeFirstInstruction)) {
        $arguments += @(
            '--serialized-identity-resolve-runtime-first-instruction',
            $ResolveRuntimeFirstInstruction
        )
    }
}
if ($EnablePPtrObserver) {
    $arguments += @(
        '--serialized-pptr-rva', '0x23a94bc',
        '--serialized-pptr-first-instruction', '0xd10183ff',
        '--serialized-pptr-target-architecture', $(if ($AllowTranslatedHostHook) {
            'host-translated'
        } else {
            'arm64-guest'
        }),
        '--serialized-pptr-guest-patch', $(if ($EnablePPtrGuestPatch) { '1' } else { '0' })
    )
}
if ($EnableObjectLookupObserver) {
    $arguments += @(
        '--serialized-object-lookup-observer', '1',
        '--serialized-object-lookup-rva', '0x1c9be74',
        '--serialized-object-lookup-first-instruction', '0x2a0003e8',
        '--serialized-object-lookup-target-architecture', 'arm64-guest'
    )
}
if ($EnableObjectResolveCandidateObserver) {
    $arguments += @(
        '--serialized-object-resolve-candidate-rva', '0x23a9a7c',
        '--serialized-object-resolve-candidate-first-instruction', '0xa9be57fe',
        '--serialized-object-resolve-candidate-target-architecture', $(if ($AllowTranslatedHostHook) {
            'host-translated'
        } else {
            'arm64-guest'
        }),
        '--serialized-object-resolve-candidate-pptr-caller-rva', '0x23abd04',
        '--serialized-object-resolve-candidate-pptr-to-resolve-caller-delta', '0x14'
    )
}
if ($EnableSerializedFieldReadObserver) {
    $fieldRva = '0x23c87b8'
    $fieldFirstInstruction = '0xd103c3ff'
    if ($EnableMaterialFieldTransferObserver) {
        $fieldRva = '0x23c77a4'
        $fieldFirstInstruction = '0xf81a0ffe'
    }
    $arguments += @(
        '--serialized-field-read-observer', '1',
        '--serialized-field-read-rva', $fieldRva,
        '--serialized-field-read-first-instruction', $fieldFirstInstruction,
        '--serialized-field-read-target-architecture', 'arm64-guest'
    )
}
if ($EnableContextObserver) {
    $arguments += @('--context-observer-all', '0')
}
if ($EnableContextObserverAll) {
    $arguments += @('--context-observer-all', '1')
}
$env:MY_HOOK_SERIALIZED_FIELD_READ_MODE = if ($EnableMaterialFieldTransferObserver) { 'transfer' } else { '' }
$previousMShaderCallsiteObserver = $env:SR_HSR45_MSHADER_CALLSITE_OBSERVER
$env:SR_HSR45_MSHADER_CALLSITE_OBSERVER = if ($EnableMaterialMShaderCallsiteObserver) { '1' } else { '0' }
$env:MY_HOOK_SERIALIZED_FIELD_READ_MAX_RECORDS = $SerializedFieldReadMaxRecords.ToString()
$env:MY_HOOK_SERIALIZED_FIELD_READ_NAMES = if ($null -eq $SerializedFieldReadNames) { '' } else { $SerializedFieldReadNames }
$env:MY_HOOK_SERIALIZED_OBJECT_LOAD_BOUNDARY_MAX_RECORDS = $LoadBoundaryMaxRecords.ToString()
$env:MY_HOOK_SERIALIZED_OBJECT_CONSUMER_MAX_RECORDS = $ConsumerMaxRecords.ToString()
$env:MY_HOOK_SERIALIZED_OBJECT_RESOLVE_CANDIDATE_MAX_CALLS = $ResolveCandidateMaxCalls.ToString()
$previousObjectLookupMaxRecords = $env:MY_HOOK_SERIALIZED_OBJECT_LOOKUP_MAX_RECORDS
$env:MY_HOOK_SERIALIZED_OBJECT_LOOKUP_MAX_RECORDS = $ObjectLookupMaxRecords.ToString()
$previousPPtrScopeMode = $env:MY_HOOK_HSR45_PPTR_SCOPE_MODE
$previousPPtrCallsiteRva = $env:SR_SERIALIZED_PPTR_CALLSITE_RVA
$previousPPtrCallsiteReturnRva = $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_RVA
$previousPPtrCallsiteReturnInstruction = $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_FIRST_INSTRUCTION
$previousPPtrCallsiteKeyOffset = $env:SR_SERIALIZED_PPTR_CALLSITE_KEY_STACK_OFFSET
$previousPPtrCallsiteOutputOffset = $env:SR_SERIALIZED_PPTR_CALLSITE_OUTPUT_STACK_OFFSET
$previousPPtrCallsiteLookupCallerRva = $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_CALLER_RVA
$previousPPtrCallsiteLookupBranchInstruction = $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_BRANCH_INSTRUCTION
$previousPPtrCallsiteMaxRecords = $env:SR_SERIALIZED_PPTR_CALLSITE_MAX_RECORDS
$env:MY_HOOK_HSR45_PPTR_SCOPE_MODE = $PPtrScopeMode
if (-not [string]::IsNullOrWhiteSpace($PPtrCallsiteRva)) {
    $env:SR_SERIALIZED_PPTR_CALLSITE_RVA = $PPtrCallsiteRva
    $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_RVA = $PPtrCallsiteReturnRva
    $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_FIRST_INSTRUCTION = $PPtrCallsiteReturnFirstInstruction
    $env:SR_SERIALIZED_PPTR_CALLSITE_KEY_STACK_OFFSET = $PPtrCallsiteKeyStackOffset
    $env:SR_SERIALIZED_PPTR_CALLSITE_OUTPUT_STACK_OFFSET = $PPtrCallsiteOutputStackOffset
    $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_CALLER_RVA = $PPtrCallsiteLookupCallerRva
    $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_BRANCH_INSTRUCTION = $PPtrCallsiteLookupBranchInstruction
    $env:SR_SERIALIZED_PPTR_CALLSITE_MAX_RECORDS = $PPtrCallsiteMaxRecords
} else {
    $env:SR_SERIALIZED_PPTR_CALLSITE_RVA = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_RVA = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_FIRST_INSTRUCTION = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_KEY_STACK_OFFSET = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_OUTPUT_STACK_OFFSET = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_CALLER_RVA = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_BRANCH_INSTRUCTION = ''
    $env:SR_SERIALIZED_PPTR_CALLSITE_MAX_RECORDS = ''
}
if ($EnableSerializedFieldCallerObserver) {
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_OBSERVER = '1'
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_RVA = $SerializedFieldCallerRva
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_FIRST_INSTRUCTION = $SerializedFieldCallerFirstInstruction
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_TARGET_ARCHITECTURE = $SerializedFieldCallerTargetArchitecture
} else {
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_OBSERVER = '0'
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_RVA = ''
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_FIRST_INSTRUCTION = ''
    $env:MY_HOOK_SERIALIZED_FIELD_CALLER_TARGET_ARCHITECTURE = ''
}
if ($EnableIdentityCallerPatch) {
    $arguments += @('--serialized-identity-caller-patch', '1')
}
if (-not [string]::IsNullOrWhiteSpace($AdbSerial)) {
    $arguments += @('--adb-serial', $AdbSerial)
}

& $dotnet $tool @arguments
$env:SR_HSR45_MSHADER_CALLSITE_OBSERVER = $previousMShaderCallsiteObserver
$env:MY_HOOK_HSR45_PPTR_SCOPE_MODE = $previousPPtrScopeMode
$env:SR_SERIALIZED_PPTR_CALLSITE_RVA = $previousPPtrCallsiteRva
$env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_RVA = $previousPPtrCallsiteReturnRva
$env:SR_SERIALIZED_PPTR_CALLSITE_RETURN_FIRST_INSTRUCTION = $previousPPtrCallsiteReturnInstruction
$env:SR_SERIALIZED_PPTR_CALLSITE_KEY_STACK_OFFSET = $previousPPtrCallsiteKeyOffset
$env:SR_SERIALIZED_PPTR_CALLSITE_OUTPUT_STACK_OFFSET = $previousPPtrCallsiteOutputOffset
$env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_CALLER_RVA = $previousPPtrCallsiteLookupCallerRva
$env:SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_BRANCH_INSTRUCTION = $previousPPtrCallsiteLookupBranchInstruction
$env:SR_SERIALIZED_PPTR_CALLSITE_MAX_RECORDS = $previousPPtrCallsiteMaxRecords
$env:MY_HOOK_SERIALIZED_OBJECT_LOOKUP_MAX_RECORDS = $previousObjectLookupMaxRecords
exit $LASTEXITCODE
