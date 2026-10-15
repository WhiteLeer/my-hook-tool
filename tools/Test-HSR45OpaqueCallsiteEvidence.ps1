param(
    [string]$AuditScript = (Join-Path $PSScriptRoot 'Audit-HSR45OpaqueCallsiteEvidence.ps1')
)

$ErrorActionPreference = 'Stop'
$tempStem = Join-Path $env:TEMP ('hsr45-opaque-audit-' + [guid]::NewGuid().ToString('N'))
$eventsPath = "$tempStem.ndjson"
$auditPath = "$tempStem.json"
$install = [ordered]@{
    format = 'my-hook.runtime-event.v1'
    kind = 'unity.hsr45.opaque-callsite-observer-installed'
    status = 'installed'
    runtimeArchitecture = 'arm64'
    captureSessionId = 'synthetic-session'
    processId = 100
    threadId = 200
    moduleBase = '0x400021200000'
    parentInstallSitePrologueMatches = $true
    resolverInstallSitePrologueMatches = $true
    producerInputWindowMatches = $true
    producerReturnWindowMatches = $true
    consumerArgumentWindowMatches = $true
    resolverArgumentWindowMatches = $true
    producerInputProbeStatus = 0
    producerReturnProbeStatus = 0
    consumerArgumentProbeStatus = 0
    resolverArgumentProbeStatus = 0
}
$scope = @{
    status = 'active'
    invocationId = 7
    wrapperRva = '0x23a94bc'
}
$common = @{
    format = 'my-hook.runtime-event.v1'
    runtimeArchitecture = 'arm64'
    captureSessionId = 'synthetic-session'
    processId = 100
    threadId = 200
}
$events = @(
    (($common + @{
        kind = 'unity.serialized-pptr-observer-installed'
        status = 'installed'
        hookPath = 'dobby-instrument-caller-entry-return'
        scopeMode = 'both'
        scopeEntryRva = '0x23abcfc'
        scopeEntryInstruction = '0xb90023ff'
        scopeCallerRva = '0x23abd00'
        scopeCallerInstruction = '0x97fff5ef'
        scopeExitRva = '0x23abd04'
        scopeExitInstruction = '0xb94023f9'
        installSitePrologueMatches = $true
        scopeEntryWindowMatches = $true
        scopeExitWindowMatches = $true
        scopeEntryProbeStatus = 0
        scopeExitProbeStatus = 0
    }) | ConvertTo-Json -Compress -Depth 8)
    ($install | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-producer-input'
        status = 'observed'
        instructionRva = '0x23a95a8'
        parentInvocationScope = $scope
        opaqueRecordWord0 = '0x11'
        opaqueRecordWord8 = '0x22'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-producer-input-raw'
        status = 'observed'
        instructionRva = '0x23a95a8'
        parentScopeMatched = $false
        stackPointer = '0x1000'
        registerX8 = '0x11'
        registerX25 = '0x22'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-resolver-arguments-raw'
        status = 'observed'
        instructionRva = '0x23a9210'
        nextCallRva = '0x23a9214'
        parentScopeMatched = $false
        resolverCallId = 100
        opaqueArgument1_32 = '0x1234'
        opaqueArgument2_32 = '0x3'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-producer-return'
        status = 'observed'
        instructionRva = '0x23a95b0'
        parentInvocationScope = $scope
        opaqueResult32 = '0x1234'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-consumer-argument'
        status = 'observed'
        instructionRva = '0x23a9610'
        parentInvocationScope = $scope
        opaqueArgument1_32 = '0x1234'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.hsr45.opaque-resolver-arguments'
        status = 'observed'
        instructionRva = '0x23a9210'
        parentInvocationScope = $scope
        resolverCallId = 99
        opaqueArgument1_32 = '0x1234'
        opaqueArgument2_32 = '0x3'
    }) | ConvertTo-Json -Compress -Depth 8)
    (($common + @{
        kind = 'unity.serialized-object-resolve-candidate'
        status = 'observed'
        parentInvocationScope = @{ invocationId = 7; wrapperRva = '0x23a94bc' }
        sameParentResolverCall = @{
            status = 'matched'
            resolverCallId = 99
            parentInvocationId = 7
            argumentRegistersMatched = $true
            callsiteReturnPcMatched = $true
        }
        argument1_32 = '0x1234'
        argument2_32 = '0x3'
        caller = '0x4000235a9218'
        returnTypeObserved = $true
        targetObjectType = $true
        returnVtableObservedType = @{ persistentTypeId = 21 }
    }) | ConvertTo-Json -Compress -Depth 8)
)

function Invoke-Audit([string[]]$Lines) {
    [IO.File]::WriteAllLines($eventsPath, $Lines)
    & $AuditScript -Events $eventsPath -Output $auditPath | Out-Null
    $exitCode = $LASTEXITCODE
    $result = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{ ExitCode = $exitCode; Result = $result }
}

$positive = Invoke-Audit $events
if ($positive.ExitCode -ne 0 -or $positive.Result.acceptedOpaqueToTypedLinks -ne 1) {
    throw 'Positive same-invocation fixture was not accepted exactly once.'
}
if ($positive.Result.unscopedProducerInputObservations -ne 1) {
    throw 'Unscoped raw producer observation was not counted separately.'
}
if ($positive.Result.unscopedResolverArgumentObservations -ne 1) {
    throw 'Unscoped raw resolver-argument observation was not counted separately.'
}

$directCallsiteInstall = $common.Clone()
$directCallsiteInstall.kind = 'unity.hsr45.opaque-callsite-observer-installed'
$directCallsiteInstall.status = 'installed'
$directCallsiteInstall.moduleBase = '0x400021200000'
$directCallsiteInstall.resolverArgumentAddress = '0x4000235a9210'
$directCallsiteInstall.resolverArgumentWords = '0x2a1403e2,0x9400021a,0xaa0003f6'
$directCallsiteInstall.resolverArgumentWindowMatches = $true
$directCallsiteInstall.resolverCallTargetRva = '0x23a9a7c'
$directCallsiteInstall.resolverCallTargetMatchesObserver = $true
$directCallsiteInstall.probeMask = 8
$directCallsiteInstall.resolverArgumentProbeStatus = 0
$typedCandidateInstall = $common.Clone()
$typedCandidateInstall.kind = 'unity.serialized-object-resolve-candidate-observer-installed'
$typedCandidateInstall.status = 'installed'
$typedCandidateInstall.actualFirstInstruction = '0xa9be57fe'
$typedCandidateInstall.installSitePrologueMatches = $true
$directCandidate = $events[-1] | ConvertFrom-Json -AsHashtable
$directCandidate.sameRawResolverCallsiteInvocation = @{
    evidence = 'same-thread token set at a statically verified direct BL callsite; exact argument registers matched at hooked callee entry'
    callsiteRva = '0x23a9210'
    callInstructionRva = '0x23a9214'
    callTargetRva = '0x23a9a7c'
    callsiteTargetVerified = $true
    observationId = 100
    rawProbeEventEmitted = $true
    callerReturnPc = '0x707205d7b1a0'
    argumentsMatched = $true
    targetTypeObserved = $true
}
$directCandidate.caller = '0x707205d7b1a0'
$directCandidate.returnCandidate = '0x700000001000'
$directCandidate.returnVtable = '0x700000000100'
$directCandidate.returnVtableReadable = $true
$directCandidate.returnVtableObservedType = @{ persistentTypeId = 21; className = 'Material'; objectSize = 264 }
$directFixture = @($events) + @(
    ($directCallsiteInstall | ConvertTo-Json -Compress -Depth 8),
    ($typedCandidateInstall | ConvertTo-Json -Compress -Depth 8),
    ($directCandidate | ConvertTo-Json -Compress -Depth 8)
)
$directResult = Invoke-Audit $directFixture
if ($directResult.Result.verifiedCallsiteToTypedReturnLinks -ne 1 -or
    $directResult.Result.endToEndIdentityBridge -ne 'not_proven') {
    throw 'Verified direct callsite-to-typed-callee evidence was not separated from end-to-end identity.'
}

$directOnlyFixture = @($events | Where-Object {
    (($_ | ConvertFrom-Json).kind) -ne 'unity.serialized-pptr-observer-installed'
}) + @(
    ($directCallsiteInstall | ConvertTo-Json -Compress -Depth 8),
    ($typedCandidateInstall | ConvertTo-Json -Compress -Depth 8),
    ($directCandidate | ConvertTo-Json -Compress -Depth 8)
)
$directOnlyResult = Invoke-Audit $directOnlyFixture
if ($directOnlyResult.Result.verifiedCallsiteToTypedReturnLinks -ne 1 -or
    $directOnlyResult.Result.endToEndIdentityBridge -ne 'not_proven' -or
    $directOnlyResult.Result.safePPtrScopeInstallStatus -ne 'not_installed_or_not_observed') {
    throw 'Direct callsite-to-typed-return evidence incorrectly depended on the PPtr scope observer.'
}

$rateLimitedCandidate = $directCandidate | ConvertTo-Json -Compress -Depth 8 | ConvertFrom-Json -AsHashtable
$rateLimitedCandidate.sameRawResolverCallsiteInvocation.rawProbeEventEmitted = $false
$rateLimitedEvents = @($events | Where-Object {
    (($_ | ConvertFrom-Json).kind) -ne 'unity.hsr45.opaque-resolver-arguments-raw'
}) + @(
    ($directCallsiteInstall | ConvertTo-Json -Compress -Depth 8),
    ($typedCandidateInstall | ConvertTo-Json -Compress -Depth 8),
    ($rateLimitedCandidate | ConvertTo-Json -Compress -Depth 8)
)
$rateLimitedResult = Invoke-Audit $rateLimitedEvents
if ($rateLimitedResult.Result.verifiedCallsiteToTypedReturnLinks -ne 1 -or
    $rateLimitedResult.Result.endToEndIdentityBridge -ne 'not_proven' -or
    $rateLimitedResult.Result.verifiedCallsiteToTypedReturns[0].rawProbeEventEmitted -ne $false) {
    throw 'A self-contained exact callsite token with an explicitly rate-limited raw row was not accepted.'
}
$duplicateRateLimitedToken = @($rateLimitedEvents) + @($rateLimitedEvents[-1])
$duplicateRateLimitedResult = Invoke-Audit $duplicateRateLimitedToken
if ($duplicateRateLimitedResult.Result.verifiedCallsiteToTypedReturnLinks -ne 0 -or
    $duplicateRateLimitedResult.Result.rejectedCallsiteToTypedReturnLinks -ne 2) {
    throw 'Duplicate typed returns sharing one rate-limited callsite token were accepted.'
}
$inconsistentRateLimit = @($events) + @(
    ($directCallsiteInstall | ConvertTo-Json -Compress -Depth 8),
    ($typedCandidateInstall | ConvertTo-Json -Compress -Depth 8),
    ($rateLimitedCandidate | ConvertTo-Json -Compress -Depth 8)
)
$inconsistentRateLimitResult = Invoke-Audit $inconsistentRateLimit
if ($inconsistentRateLimitResult.Result.verifiedCallsiteToTypedReturnLinks -ne 0) {
    throw 'A rate-limited callsite candidate with a contradictory raw event was accepted.'
}

$delayedTypeCandidate = $directCandidate | ConvertTo-Json -Compress -Depth 8 | ConvertFrom-Json -AsHashtable
$delayedTypeCandidate.returnTypeObserved = $false
$delayedTypeCandidate.targetObjectType = $false
$delayedTypeCandidate.Remove('returnVtableObservedType')
$delayedTypeCandidate.sameRawResolverCallsiteInvocation.targetTypeObserved = $false
$delayedTypeTransfer = $common.Clone()
$delayedTypeTransfer.kind = 'unity.transfer'
$delayedTypeTransfer.vtable = '0x700000000100'
$delayedTypeTransfer.typeId = 21
$delayedTypeTransfer.typeName = 'Material'
$delayedTypeTransfer.objectSize = 264
$delayedTypeFixture = @($events) + @(
    ($directCallsiteInstall | ConvertTo-Json -Compress -Depth 8),
    ($typedCandidateInstall | ConvertTo-Json -Compress -Depth 8),
    ($delayedTypeCandidate | ConvertTo-Json -Compress -Depth 8),
    ($delayedTypeTransfer | ConvertTo-Json -Compress -Depth 8)
)
$delayedTypeResult = Invoke-Audit $delayedTypeFixture
if ($delayedTypeResult.Result.verifiedCallsiteToTypedReturnLinks -ne 1 -or
    $delayedTypeResult.Result.verifiedCallsiteToTypedReturns[0].observedClassName -ne 'Material' -or
    $delayedTypeResult.Result.verifiedCallsiteToTypedReturns[0].returnTypeEvidence -notmatch 'exact same-process vtable equality') {
    throw 'Exact delayed vtable-to-Transfer type attribution was not accepted.'
}
$wrongVtableFixture = @($delayedTypeFixture)
$wrongVtable = $wrongVtableFixture[-1] | ConvertFrom-Json
$wrongVtable.vtable = '0x700000000108'
$wrongVtableFixture[-1] = $wrongVtable | ConvertTo-Json -Compress -Depth 8
$wrongVtableResult = Invoke-Audit $wrongVtableFixture
if ($wrongVtableResult.Result.verifiedCallsiteToTypedReturnLinks -ne 0) {
    throw 'Transfer type from a different vtable was accepted.'
}

$entryOnlyInstallFixture = @($directFixture)
$entryOnlyPPtrInstall = $entryOnlyInstallFixture[0] | ConvertFrom-Json
$entryOnlyPPtrInstall.scopeMode = 'entry-only'
$entryOnlyPPtrInstall.scopeExitProbeStatus = -1
$entryOnlyInstallFixture[0] = $entryOnlyPPtrInstall | ConvertTo-Json -Compress -Depth 8
$entryOnlyInstallResult = Invoke-Audit $entryOnlyInstallFixture
if ($entryOnlyInstallResult.Result.installStatus -ne 'installed' -or
    $entryOnlyInstallResult.Result.opaqueResolverProbeInstallStatus -ne 'installed' -or
    $entryOnlyInstallResult.Result.safePPtrScopeInstallStatus -ne 'not_installed_or_not_observed') {
    throw 'Entry-only PPtr plus installed opaque resolver probes were misreported as uninstalled or as a safe full scope.'
}

$directCandidateIndex = -1
for ($index = 0; $index -lt $directFixture.Count; ++$index) {
    $fixtureEvent = $directFixture[$index] | ConvertFrom-Json
    if ($fixtureEvent.kind -eq 'unity.serialized-object-resolve-candidate' -and
        $fixtureEvent.sameRawResolverCallsiteInvocation) {
        $directCandidateIndex = $index
        break
    }
}
if ($directCandidateIndex -lt 0) { throw 'Direct typed-candidate fixture is missing.' }

$wrongDirectToken = @($directFixture)
$wrongDirectCandidate = $wrongDirectToken[$directCandidateIndex] | ConvertFrom-Json
$wrongDirectCandidate.sameRawResolverCallsiteInvocation.observationId = 101
$wrongDirectToken[$directCandidateIndex] = $wrongDirectCandidate | ConvertTo-Json -Compress -Depth 8
$wrongDirectResult = Invoke-Audit $wrongDirectToken
if ($wrongDirectResult.Result.verifiedCallsiteToTypedReturnLinks -ne 0) {
    throw 'Mismatched direct callsite token was accepted.'
}

$wrongCallTarget = @($directFixture)
$wrongTargetCandidate = $wrongCallTarget[$directCandidateIndex] | ConvertFrom-Json
$wrongTargetCandidate.sameRawResolverCallsiteInvocation.callsiteTargetVerified = $false
$wrongCallTarget[$directCandidateIndex] = $wrongTargetCandidate | ConvertTo-Json -Compress -Depth 8
$wrongCallTargetResult = Invoke-Audit $wrongCallTarget
if ($wrongCallTargetResult.Result.verifiedCallsiteToTypedReturnLinks -ne 0) {
    throw 'Unverified direct-call target was accepted.'
}

$wrongCallToken = @($events)
$candidate = $wrongCallToken[$wrongCallToken.Count - 1] | ConvertFrom-Json
$candidate.sameParentResolverCall.resolverCallId = 100
$wrongCallToken[-1] = $candidate | ConvertTo-Json -Compress -Depth 8
$wrongTokenResult = Invoke-Audit $wrongCallToken
if ($wrongTokenResult.ExitCode -ne 2 -or
    $wrongTokenResult.Result.acceptedOpaqueToTypedLinks -ne 0) {
    throw 'Mismatched resolverCallId was not rejected.'
}

$duplicateCandidate = @($events)
$duplicateCandidate += $duplicateCandidate[-1]
$duplicateCandidateResult = Invoke-Audit $duplicateCandidate
if ($duplicateCandidateResult.ExitCode -ne 2 -or
    $duplicateCandidateResult.Result.acceptedOpaqueToTypedLinks -ne 0 -or
    $duplicateCandidateResult.Result.rejectedScopes -ne 1) {
    throw 'Multiple typed candidates for one resolverCallId were not rejected as ambiguous.'
}

$badInstall = @($events)
$installIndex = -1
for ($index = 0; $index -lt $badInstall.Count; ++$index) {
    if (($badInstall[$index] | ConvertFrom-Json).kind -eq
        'unity.hsr45.opaque-callsite-observer-installed') {
        $installIndex = $index
        break
    }
}
if ($installIndex -lt 0) { throw 'Opaque observer install fixture is missing.' }
$installEvent = $badInstall[$installIndex] | ConvertFrom-Json
$installEvent.resolverArgumentWindowMatches = $false
$badInstall[$installIndex] = $installEvent | ConvertTo-Json -Compress -Depth 8
$badInstallResult = Invoke-Audit $badInstall
if ($badInstallResult.ExitCode -ne 2 -or
    $badInstallResult.Result.installStatus -ne 'not_installed_or_not_observed' -or
    $badInstallResult.Result.acceptedOpaqueToTypedLinks -ne 0) {
    throw 'Failed instruction guard was not rejected.'
}

$unsafePPtrPath = @($events)
$unsafePPtrInstall = $unsafePPtrPath[0] | ConvertFrom-Json
$unsafePPtrInstall.hookPath = 'dobby'
$unsafePPtrPath[0] = $unsafePPtrInstall | ConvertTo-Json -Compress -Depth 8
$unsafePPtrResult = Invoke-Audit $unsafePPtrPath
if ($unsafePPtrResult.ExitCode -ne 2 -or
    $unsafePPtrResult.Result.installStatus -ne 'not_installed_or_not_observed' -or
    $unsafePPtrResult.Result.acceptedOpaqueToTypedLinks -ne 0) {
    throw 'Replacing PPtr trampoline was incorrectly accepted as a safe parent scope.'
}

Write-Host 'PASS: safe scopes, entry-only and opaque probe install reporting, statically verified direct callee target, exact raw-argument joins, bounded raw-row omission, unique typed return per token, and deferred exact-vtable attribution; wrong tokens, targets, vtables, duplicates, failed guards, and replacing PPtr trampoline rejected.'
exit 0
