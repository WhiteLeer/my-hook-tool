param(
    [Parameter(Mandatory)][string]$Events,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$eventsPath = (Resolve-Path -LiteralPath $Events).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$outputDirectory = Split-Path -Parent $outputPath
if ($outputDirectory -and !(Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$eventsByScope = [Collections.Generic.Dictionary[string,object]]::new()
$moduleBases = [Collections.Generic.Dictionary[string,UInt64]]::new()
$installKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$safePPtrScopeInstallKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$directResolverInstallKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$typedResolverInstallKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$observedTypesByVtable = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$rawResolverCalls = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$directResolverCandidates = [Collections.Generic.List[object]]::new()
$installRecords = [Collections.Generic.List[object]]::new()
$rejectedInstallCount = 0
$rejectedProbeEventCount = 0
$unscopedProducerInputCount = 0
$unscopedResolverArgumentCount = 0
$expectedInstructionRvas = @{
    'unity.hsr45.opaque-producer-input' = '0x23a95a8'
    'unity.hsr45.opaque-producer-return' = '0x23a95b0'
    'unity.hsr45.opaque-consumer-argument' = '0x23a9610'
    'unity.hsr45.opaque-resolver-arguments' = '0x23a9210'
}

function Get-HexUInt64([string]$Value) {
    return [Convert]::ToUInt64(($Value -replace '^0x', ''), 16)
}

function Get-ProcessKey($Event) {
    return '{0}|{1}' -f $Event.captureSessionId, $Event.processId
}

function Get-ScopeKey($Event) {
    $scope = $Event.parentInvocationScope
    if ($null -eq $scope -or $null -eq $scope.invocationId) { return $null }
    if ($scope.status -and $scope.status -ne 'active') { return $null }
    return '{0}|{1}|{2}|{3}' -f $Event.captureSessionId, $Event.processId,
        $Event.threadId, $scope.invocationId
}

function Get-CallsiteInvocationKey($Event, [uint64]$ObservationId) {
    return '{0}|{1}|{2}|{3}' -f $Event.captureSessionId, $Event.processId,
        $Event.threadId, $ObservationId
}

function Get-VtableKey($Event, [string]$Vtable) {
    return '{0}|0x{1:x}' -f (Get-ProcessKey $Event), (Get-HexUInt64 $Vtable)
}

function Add-ScopeEvent([string]$Key, $Event) {
    if (!$eventsByScope.ContainsKey($Key)) {
        $eventsByScope[$Key] = [ordered]@{
            producerInput = [Collections.Generic.List[object]]::new()
            producerReturn = [Collections.Generic.List[object]]::new()
            consumerArgument = [Collections.Generic.List[object]]::new()
            resolverArguments = [Collections.Generic.List[object]]::new()
            resolverCandidates = [Collections.Generic.List[object]]::new()
        }
    }
    $group = $eventsByScope[$Key]
    switch ($Event.kind) {
        'unity.hsr45.opaque-producer-input' { $group.producerInput.Add($Event) }
        'unity.hsr45.opaque-producer-return' { $group.producerReturn.Add($Event) }
        'unity.hsr45.opaque-consumer-argument' { $group.consumerArgument.Add($Event) }
        'unity.hsr45.opaque-resolver-arguments' { $group.resolverArguments.Add($Event) }
        'unity.serialized-object-resolve-candidate' { $group.resolverCandidates.Add($Event) }
    }
}

foreach ($line in [IO.File]::ReadLines($eventsPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $event = $line | ConvertFrom-Json
    if ($event.kind -eq 'unity.transfer' -and
        $event.runtimeArchitecture -eq 'arm64' -and
        $event.vtable -and $null -ne $event.typeId -and
        $event.typeName -and $event.objectSize -gt 0) {
        $vtableKey = Get-VtableKey $event $event.vtable
        if (!$observedTypesByVtable.ContainsKey($vtableKey)) {
            $observedTypesByVtable[$vtableKey] = [Collections.Generic.List[object]]::new()
        }
        $observedTypesByVtable[$vtableKey].Add([pscustomobject]@{
            persistentTypeId = [int]$event.typeId
            className = [string]$event.typeName
            objectSize = [int]$event.objectSize
        })
        continue
    }
    if ($event.kind -eq 'unity.serialized-pptr-observer-installed') {
        $safeScopeInstall = $event.status -eq 'installed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.hookPath -eq 'dobby-instrument-caller-entry-return' -and
            $event.scopeMode -eq 'both' -and
            $event.scopeEntryRva -eq '0x23abcfc' -and
            $event.scopeEntryInstruction -eq '0xb90023ff' -and
            $event.scopeCallerRva -eq '0x23abd00' -and
            $event.scopeCallerInstruction -eq '0x97fff5ef' -and
            $event.scopeExitRva -eq '0x23abd04' -and
            $event.scopeExitInstruction -eq '0xb94023f9' -and
            $event.installSitePrologueMatches -eq $true -and
            $event.scopeEntryWindowMatches -eq $true -and
            $event.scopeExitWindowMatches -eq $true -and
            $event.scopeEntryProbeStatus -eq 0 -and
            $event.scopeExitProbeStatus -eq 0
        if ($safeScopeInstall) {
            $safePPtrScopeInstallKeys.Add((Get-ProcessKey $event)) | Out-Null
        } else {
            $rejectedInstallCount++
        }
    }
    if ($event.kind -eq 'unity.hsr45.opaque-callsite-observer-installed') {
        $installRecords.Add($event)
        $probeStatusesPassed = @(
            $event.producerInputProbeStatus,
            $event.producerReturnProbeStatus,
            $event.consumerArgumentProbeStatus,
            $event.resolverArgumentProbeStatus
        ) | Where-Object { $_ -ne 0 }
        $guardsPassed = $event.status -eq 'installed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.parentInstallSitePrologueMatches -eq $true -and
            $event.resolverInstallSitePrologueMatches -eq $true -and
            $event.producerInputWindowMatches -eq $true -and
            $event.producerReturnWindowMatches -eq $true -and
            $event.consumerArgumentWindowMatches -eq $true -and
            $event.resolverArgumentWindowMatches -eq $true -and
            $probeStatusesPassed.Count -eq 0 -and
            $event.moduleBase -and
            $safePPtrScopeInstallKeys.Contains((Get-ProcessKey $event))
        if ($guardsPassed) {
            $processKey = Get-ProcessKey $event
            $installKeys.Add($processKey) | Out-Null
            $moduleBases[$processKey] = Get-HexUInt64 $event.moduleBase
        } else {
            $rejectedInstallCount++
        }
        $moduleBase = if ($event.moduleBase) { Get-HexUInt64 $event.moduleBase } else { 0 }
        $expectedResolverArgumentAddress = '0x{0:x}' -f ($moduleBase + 0x23a9210)
        $directResolverInstallPassed = $event.status -eq 'installed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.moduleBase -and
            $event.resolverArgumentAddress -eq $expectedResolverArgumentAddress -and
            $event.resolverArgumentWords -eq '0x2a1403e2,0x9400021a,0xaa0003f6' -and
            $event.resolverCallTargetRva -eq '0x23a9a7c' -and
            $event.resolverCallTargetMatchesObserver -eq $true -and
            $event.resolverArgumentWindowMatches -eq $true -and
            (($event.probeMask -band 8) -ne 0) -and
            $event.resolverArgumentProbeStatus -eq 0
        if ($directResolverInstallPassed) {
            $directResolverInstallKeys.Add((Get-ProcessKey $event)) | Out-Null
        }
    }
    if ($event.kind -eq 'unity.serialized-object-resolve-candidate-observer-installed' -and
        $event.status -eq 'installed' -and
        $event.runtimeArchitecture -eq 'arm64' -and
        $event.actualFirstInstruction -eq '0xa9be57fe' -and
        $event.installSitePrologueMatches -eq $true) {
        $typedResolverInstallKeys.Add((Get-ProcessKey $event)) | Out-Null
    }
    if ($event.kind -eq 'unity.hsr45.opaque-resolver-arguments-raw') {
        if ($event.status -eq 'observed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.instructionRva -eq '0x23a9210' -and
            $event.nextCallRva -eq '0x23a9214' -and
            $event.parentScopeMatched -eq $false -and
            $null -eq $event.parentInvocationScope -and
            $null -ne $event.resolverCallId) {
            $unscopedResolverArgumentCount++
            $key = Get-CallsiteInvocationKey $event ([uint64]$event.resolverCallId)
            if (!$rawResolverCalls.ContainsKey($key)) {
                $rawResolverCalls[$key] = [Collections.Generic.List[object]]::new()
            }
            $rawResolverCalls[$key].Add($event)
        } else {
            $rejectedProbeEventCount++
        }
        continue
    }
    if ($event.kind -eq 'unity.serialized-object-resolve-candidate' -and
        $null -ne $event.sameRawResolverCallsiteInvocation) {
        $directResolverCandidates.Add($event)
    }
    if ($event.kind -eq 'unity.hsr45.opaque-producer-input-raw') {
        if ($event.status -eq 'observed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.instructionRva -eq '0x23a95a8' -and
            $event.parentScopeMatched -eq $false -and
            $null -eq $event.parentInvocationScope) {
            $unscopedProducerInputCount++
        } else {
            $rejectedProbeEventCount++
        }
        continue
    }
    if ($event.kind -eq 'unity.hsr45.opaque-resolver-arguments-raw') {
        if ($event.status -eq 'observed' -and
            $event.runtimeArchitecture -eq 'arm64' -and
            $event.instructionRva -eq '0x23a9210' -and
            $event.parentScopeMatched -eq $false -and
            $null -eq $event.parentInvocationScope) {
            $unscopedResolverArgumentCount++
        } else {
            $rejectedProbeEventCount++
        }
        continue
    }
    if ($event.kind -in @(
        'unity.hsr45.opaque-producer-input',
        'unity.hsr45.opaque-producer-return',
        'unity.hsr45.opaque-consumer-argument',
        'unity.hsr45.opaque-resolver-arguments',
        'unity.serialized-object-resolve-candidate')) {
        if (!$installKeys.Contains((Get-ProcessKey $event))) { continue }
        if ($event.runtimeArchitecture -ne 'arm64' -or
            $event.status -ne 'observed' -or
            $event.parentInvocationScope.wrapperRva -ne '0x23a94bc') {
            $rejectedProbeEventCount++
            continue
        }
        if ($expectedInstructionRvas.ContainsKey($event.kind) -and
            $event.instructionRva -ne $expectedInstructionRvas[$event.kind]) {
            $rejectedProbeEventCount++
            continue
        }
        $key = Get-ScopeKey $event
        if ($null -ne $key) { Add-ScopeEvent $key $event }
    }
}

$accepted = [Collections.Generic.List[object]]::new()
$rejected = [Collections.Generic.List[object]]::new()
foreach ($entry in $eventsByScope.GetEnumerator()) {
    $group = $entry.Value
    if (($group.producerInput.Count -ne 1) -or
        ($group.producerReturn.Count -ne 1) -or
        ($group.consumerArgument.Count -ne 1)) {
        if (($group.producerInput.Count -gt 0) -or
            ($group.producerReturn.Count -gt 0) -or
            ($group.consumerArgument.Count -gt 0)) {
            $rejected.Add([pscustomobject]@{
                scope = $entry.Key
                reason = 'producer-consumer observation count is not exactly one'
                producerInputCount = $group.producerInput.Count
                producerReturnCount = $group.producerReturn.Count
                consumerArgumentCount = $group.consumerArgument.Count
            })
        }
        continue
    }

    $inputEvent = $group.producerInput[0]
    $returnEvent = $group.producerReturn[0]
    $consumerEvent = $group.consumerArgument[0]
    $scope = $inputEvent.parentInvocationScope
    $sameScope = (
        ($returnEvent.parentInvocationScope.invocationId -eq $scope.invocationId) -and
        ($consumerEvent.parentInvocationScope.invocationId -eq $scope.invocationId) -and
        ($inputEvent.threadId -eq $returnEvent.threadId) -and
        ($returnEvent.threadId -eq $consumerEvent.threadId) -and
        ($inputEvent.processId -eq $returnEvent.processId) -and
        ($returnEvent.processId -eq $consumerEvent.processId)
    )
    $sameValue = $returnEvent.opaqueResult32 -eq $consumerEvent.opaqueArgument1_32
    if (!$sameScope -or !$sameValue) {
        $rejected.Add([pscustomobject]@{
            scope = $entry.Key
            reason = 'parent scope, process/thread, or opaque32 forwarding mismatch'
            producerResult32 = $returnEvent.opaqueResult32
            consumerArgument1_32 = $consumerEvent.opaqueArgument1_32
        })
        continue
    }

    $resolverCalls = @($group.resolverArguments | Where-Object {
        $_.parentInvocationScope.invocationId -eq $scope.invocationId -and
        $_.opaqueArgument1_32 -eq $consumerEvent.opaqueArgument1_32
    })
    $matchingCandidates = @($group.resolverCandidates | Where-Object {
        $candidate = $_
        $matchingCall = @($resolverCalls | Where-Object {
            $_.resolverCallId -eq $candidate.sameParentResolverCall.resolverCallId
        })
        ($candidate.sameParentResolverCall.status -eq 'matched') -and
        ($candidate.sameParentResolverCall.parentInvocationId -eq $scope.invocationId) -and
        ($candidate.sameParentResolverCall.argumentRegistersMatched -eq $true) -and
        ($candidate.sameParentResolverCall.callsiteReturnPcMatched -eq $true) -and
        ($candidate.argument1_32 -eq $consumerEvent.opaqueArgument1_32) -and
        ($matchingCall.Count -eq 1) -and
        ($matchingCall[0].opaqueArgument2_32 -eq $candidate.argument2_32)
    })
    $typedCandidates = @()
    foreach ($candidate in $matchingCandidates) {
        $procKey = Get-ProcessKey $candidate
        if (!$moduleBases.ContainsKey($procKey)) { continue }
        $callerRva = (Get-HexUInt64 $candidate.caller) - $moduleBases[$procKey]
        $typeId = $candidate.returnVtableObservedType.persistentTypeId
        if (($callerRva -eq 0x23a9218) -and
            ($candidate.returnTypeObserved -eq $true) -and
            ($candidate.targetObjectType -eq $true) -and
            ($typeId -in @(21, 48))) {
            $typedCandidates += ,([pscustomobject]@{
                resolverCallId = $candidate.sameParentResolverCall.resolverCallId
                argument1_32 = $candidate.argument1_32
                argument2_32 = $candidate.argument2_32
                returnTypeId = $typeId
                callerRva = ('0x{0:x}' -f $callerRva)
            })
        }
    }
    if ($typedCandidates.Count -ne 1) {
        $rejected.Add([pscustomobject]@{
            scope = $entry.Key
            reason = if ($typedCandidates.Count -eq 0) {
                'no exact same-parent resolver call with typed Material/Shader return'
            } else {
                'multiple typed candidates match one resolver call; refusing ambiguous evidence'
            }
            matchedResolverCandidates = $matchingCandidates.Count
            typedCandidateCount = $typedCandidates.Count
        })
        continue
    }

    $accepted.Add([pscustomobject]@{
        scope = $entry.Key
        opaqueRecordWord0 = $inputEvent.opaqueRecordWord0
        opaqueRecordWord8 = $inputEvent.opaqueRecordWord8
        producerResult32 = $returnEvent.opaqueResult32
        consumerArgument1_32 = $consumerEvent.opaqueArgument1_32
        typedResolverReturns = $typedCandidates
        conclusion = 'same-invocation opaque dataflow to a typed resolver result; not PathID, Material.m_Shader, or RenderDoc EID'
    })
}

$directCallsiteLinks = [Collections.Generic.List[object]]::new()
$rejectedDirectCallsiteLinks = [Collections.Generic.List[object]]::new()
$directCandidateCounts = [Collections.Generic.Dictionary[string,int]]::new(
    [StringComparer]::Ordinal)
foreach ($candidate in $directResolverCandidates) {
    $direct = $candidate.sameRawResolverCallsiteInvocation
    $key = Get-CallsiteInvocationKey $candidate ([uint64]$direct.observationId)
    if (!$directCandidateCounts.ContainsKey($key)) {
        $directCandidateCounts[$key] = 0
    }
    $directCandidateCounts[$key]++
}
foreach ($candidate in $directResolverCandidates) {
    $direct = $candidate.sameRawResolverCallsiteInvocation
    $key = Get-CallsiteInvocationKey $candidate ([uint64]$direct.observationId)
    $calls = if ($rawResolverCalls.ContainsKey($key)) { $rawResolverCalls[$key] } else { @() }
    $uniqueCandidate = $directCandidateCounts[$key] -eq 1
    $rawProbeEventEmitted = $null
    if ($direct.PSObject.Properties.Name -contains 'rawProbeEventEmitted') {
        $rawProbeEventEmitted = [bool]$direct.rawProbeEventEmitted
    }
    $processKey = Get-ProcessKey $candidate
    $returnType = $candidate.returnVtableObservedType
    $returnTypeEvidence = if ($candidate.returnTypeObserved -eq $true) {
        'resolver-hook vtable mapped at call time'
    } else { $null }
    if ($null -eq $returnType -and $candidate.returnVtableReadable -eq $true -and
        $candidate.returnVtable -and $observedTypesByVtable.ContainsKey(
            (Get-VtableKey $candidate $candidate.returnVtable))) {
        $types = @($observedTypesByVtable[(Get-VtableKey $candidate $candidate.returnVtable)] |
            ForEach-Object { '{0}|{1}|{2}' -f $_.persistentTypeId, $_.className, $_.objectSize } |
            Select-Object -Unique)
        if ($types.Count -eq 1) {
            $parts = $types[0] -split '\|', 3
            $returnType = [pscustomobject]@{
                persistentTypeId = [int]$parts[0]
                className = $parts[1]
                objectSize = [int]$parts[2]
            }
            $returnTypeEvidence = 'exact same-process vtable equality with an observed Transfer type; this identifies class only, not serialized identity'
        }
    }
    $returnTypeId = if ($returnType) { [int]$returnType.persistentTypeId } else { 0 }
    $returnTypeName = if ($returnType) { [string]$returnType.className } else { '' }
    $typedTarget = ($returnTypeId -eq 21 -and $returnTypeName -eq 'Material') -or
        ($returnTypeId -eq 48 -and $returnTypeName -eq 'Shader')
    $valid = $direct.evidence -eq 'same-thread token set at a statically verified direct BL callsite; exact argument registers matched at hooked callee entry' -and
        $direct.callsiteRva -eq '0x23a9210' -and
        $direct.callInstructionRva -eq '0x23a9214' -and
        $direct.callTargetRva -eq '0x23a9a7c' -and
        $direct.callsiteTargetVerified -eq $true -and
        $direct.argumentsMatched -eq $true -and
        $direct.targetTypeObserved -eq $candidate.targetObjectType -and
        $direct.callerReturnPc -eq $candidate.caller -and
        $uniqueCandidate -and
        $candidate.status -eq 'observed' -and
        $candidate.runtimeArchitecture -eq 'arm64' -and
        $candidate.returnVtableReadable -eq $true -and
        (Get-HexUInt64 $candidate.returnCandidate) -ne 0 -and
        (Get-HexUInt64 $candidate.returnVtable) -ne 0 -and
        $typedTarget -and
        $directResolverInstallKeys.Contains($processKey) -and
        $typedResolverInstallKeys.Contains($processKey) -and
        (($rawProbeEventEmitted -eq $false -and $calls.Count -eq 0) -or
            (($null -eq $rawProbeEventEmitted -or $rawProbeEventEmitted -eq $true) -and
                $calls.Count -eq 1))
    if ($valid) {
        if ($calls.Count -eq 1) {
            $call = $calls[0]
            $valid = $call.opaqueArgument1_32 -eq $candidate.argument1_32 -and
                $call.opaqueArgument2_32 -eq $candidate.argument2_32
        } else {
            $valid = $rawProbeEventEmitted -eq $false -and
                $direct.argumentsMatched -eq $true
        }
    }
    if ($valid) {
        $directCallsiteLinks.Add([pscustomobject]@{
            callsiteObservationId = $direct.observationId
            threadId = $candidate.threadId
            rawArgument1_32 = $candidate.argument1_32
            rawArgument2_32 = $candidate.argument2_32
            rawProbeEventEmitted = ($calls.Count -eq 1)
            uniqueTypedReturnForCallsiteToken = $uniqueCandidate
            returnedObject = $candidate.returnCandidate
            returnVtable = $candidate.returnVtable
            observedPersistentTypeId = $returnTypeId
            observedClassName = $returnTypeName
            returnTypeEvidence = $returnTypeEvidence
            observedCallerPc = $candidate.caller
            conclusion = 'same-thread token and exact argument match at statically verified direct callee; the return object is typed from its runtime vtable; raw callsite rows may be omitted after the bounded raw-event cap; no serialized identity is implied'
        })
    } else {
        $rejectedDirectCallsiteLinks.Add([pscustomobject]@{
            callsiteObservationId = $direct.observationId
            reason = if (!$uniqueCandidate) {
                'multiple typed-return events share one callsite token'
            } else {
                'callsite guard, matching raw event, argument equality, or runtime type evidence failed'
            }
        })
    }
}

$installPassed = $installKeys.Count -gt 0 -or $directResolverInstallKeys.Count -gt 0
$result = [ordered]@{
    format = 'my-hook.hsr45-opaque-callsite-audit.v1'
    eventsSha256 = (Get-FileHash -LiteralPath $eventsPath -Algorithm SHA256).Hash
    installStatus = if ($installPassed) { 'installed' } else { 'not_installed_or_not_observed' }
    safePPtrScopeInstallStatus = if ($safePPtrScopeInstallKeys.Count -gt 0) { 'installed' } else { 'not_installed_or_not_observed' }
    opaqueResolverProbeInstallStatus = if ($directResolverInstallKeys.Count -gt 0) { 'installed' } else { 'not_installed_or_not_observed' }
    rejectedInstallRecords = $rejectedInstallCount
    safePPtrScopeInstallProcesses = $safePPtrScopeInstallKeys.Count
    rejectedProbeEvents = $rejectedProbeEventCount
    unscopedProducerInputObservations = $unscopedProducerInputCount
    unscopedResolverArgumentObservations = $unscopedResolverArgumentCount
    observedRuntimeTypesByVtable = $observedTypesByVtable.Count
    parentScopesObserved = $eventsByScope.Count
    acceptedOpaqueToTypedLinks = $accepted.Count
    verifiedCallsiteToTypedReturnLinks = $directCallsiteLinks.Count
    rejectedCallsiteToTypedReturnLinks = $rejectedDirectCallsiteLinks.Count
    rejectedScopes = $rejected.Count
    endToEndIdentityBridge = 'not_proven'
    accepted = $accepted.ToArray()
    verifiedCallsiteToTypedReturns = $directCallsiteLinks.ToArray()
    rejectedCallsiteToTypedReturns = $rejectedDirectCallsiteLinks.ToArray()
    rejected = $rejected.ToArray()
}
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputPath -Encoding UTF8
Write-Host ("Install: {0} (opaque resolver: {1}, safe PPtr scope: {2}); accepted opaque-to-typed links: {3}; rejected scopes: {4}" -f
    $result.installStatus, $result.opaqueResolverProbeInstallStatus, $result.safePPtrScopeInstallStatus,
    $result.acceptedOpaqueToTypedLinks, $result.rejectedScopes)
if ($accepted.Count -gt 0 -or $directCallsiteLinks.Count -gt 0) { exit 0 }
exit 2
