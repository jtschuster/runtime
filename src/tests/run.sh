#!/usr/bin/env bash

function print_usage {
    echo ''
    echo 'CoreCLR test runner script.'
    echo ''
    echo 'Typical command line:'
    echo ''
    echo 'src/tests/run.sh <options> [<CORE_ROOT>]'
    echo ''
    echo 'All arguments are case-insensitive. The - and -- prefixes are optional.'
    echo 'Values can use space, =, or : as separators (e.g. --jitstress=2, -jitstress:2, jitstress 2).'
    echo ''
    echo 'Optional arguments:'
    echo '  -h, --help                       : Show usage information.'
    echo '  -v, --verbose                    : Show output from each test.'
    echo '  <arch>                           : One of x64, x86, arm, arm64, loongarch64, riscv64, wasm. Defaults to current architecture.'
    echo '  -a, --arch <arch>                : Same as above, using named argument syntax.'
    echo '  <build configuration>            : One of debug, checked, release. Defaults to debug.'
    echo '  -c, --configuration <cfg>        : Same as above, using named argument syntax.'
    echo '  android                          : Set build OS to Android.'
    echo '  wasi                             : Set build OS to WASI.'
    echo '  testenv <path>                   : Script to set environment variables for tests. Also accepts --test-env=<path>.'
    echo '  testRootDir <path>               : Root directory of the test build. Also accepts --testRootDir=<path>.'
    echo '  coreRootDir <path>               : Directory to the CORE_ROOT location. Also accepts --coreRootDir=<path> or as last positional argument.'
    echo '  enableEventLogging               : Enable event logging through LTTNG.'
    echo '  sequential                       : Run tests sequentially (default is to run in parallel).'
    echo '  runcrossgen2tests                : Runs the ReadyToRun tests compiled with Crossgen2.'
    echo '  synthesizepgo                    : Runs the tests allowing crossgen2 to synthesize PGO data.'
    echo '  jitstress <n>                    : Runs the tests with DOTNET_JitStress=n. Also accepts --jitstress=<n>.'
    echo '  jitstressregs <n>                : Runs the tests with DOTNET_JitStressRegs=n. Also accepts --jitstressregs=<n>.'
    echo '  jitminopts                       : Runs the tests with DOTNET_JITMinOpts=1.'
    echo '  jitforcerelocs                   : Runs the tests with DOTNET_ForceRelocs=1.'
    echo '  gcname <n>                       : Runs the tests with DOTNET_GCName=n. Also accepts --gcname=<n>.'
    echo '  gcstresslevel <n>                : Runs the tests with DOTNET_GCStress=n (also sets timeout to 1800000).'
    echo '    0: None                                1: GC on all allocs and '"'easy'"' places'
    echo '    2: GC on transitions to preemptive GC  4: GC on every allowable JITed instr'
    echo '    8: GC on every allowable NGEN instr   16: GC only on a unique stack trace'
    echo '  gcsimulator                      : Runs the GCSimulator tests.'
    echo '  longgc                           : Runs the long GC tests. Also accepts --long-gc.'
    echo '  useServerGC                      : Enable server GC for this test run.'
    echo '  ilasmroundtrip                   : Runs ilasm round trip on the tests.'
    echo '  printLastResultsOnly             : Print the results of the last run.'
    echo '  logsDir <path>                   : Specify the logs directory (default: artifacts/log). Also accepts --logsDir=<path>.'
    echo '  runincontext                     : Run each test in an unloadable AssemblyLoadContext.'
    echo '  tieringtest                      : Run each test to encourage tier1 rejitting.'
    echo '  runnativeaottests                : Run NativeAOT compiled tests.'
    echo '  interpreter                      : Runs the tests with the interpreter enabled.'
    echo '  node                             : Runs the tests with NodeJS (wasm only).'
    echo '  timeout <n>                      : Sets the per-test timeout in milliseconds.'
    echo '  limitedDumpGeneration            : Limit core dump generation.'
    echo '  <CORE_ROOT>                      : Path to the runtime to test (last positional argument).'
}

# Exit code constants
readonly EXIT_CODE_SUCCESS=0       # Script ran normally.
readonly EXIT_CODE_EXCEPTION=1     # Script exited because something exceptional happened (e.g. bad arguments, Ctrl-C interrupt).
readonly EXIT_CODE_TEST_FAILURE=2  # Script completed successfully, but one or more tests failed.

scriptPath="$(cd "$(dirname "$BASH_SOURCE[0]")"; pwd -P)"
repoRootDir="$(cd "$scriptPath"/../..; pwd -P)"
source "$repoRootDir/eng/common/native/init-os-and-arch.sh"

# Argument variables
buildArch="$arch"
buildOS=
buildConfiguration="Debug"
testRootDir=
coreRootDir=
logsDir=
testEnv=
gcsimulator=
longgc=
limitedCoreDumps=
verbose=0
ilasmroundtrip=
printLastResultsOnly=
runSequential=0
runincontext=0
tieringtest=0
nativeaottest=0

# Track the last unrecognized positional argument as potential CORE_ROOT
__lastPositional=""
__TestTimeout=""
__ParallelType=""

while [ $# -gt 0 ]; do
    # Preserve original argument for value extraction
    __origArg="$1"

    # Extract embedded value if present (from = or : syntax)
    __embeddedValue=""
    if [[ "$__origArg" == *"="* ]]; then
        __embeddedValue="${__origArg#*=}"
        __origArg="${__origArg%%=*}"
    elif [[ "$__origArg" == *":"* ]]; then
        __embeddedValue="${__origArg#*:}"
        __origArg="${__origArg%%:*}"
    fi

    # Strip leading -, --, / prefixes and lowercase for matching
    __normArg="$__origArg"
    __normArg="${__normArg#--}"
    __normArg="${__normArg#-}"
    __normArg="${__normArg#/}"
    __normArg="$(echo "$__normArg" | tr '[:upper:]' '[:lower:]')"

    # Phase 1: Positional bare-word args (no prefix needed)
    __positionalMatch=1
    case "$__normArg" in
        h|help|\?)
            print_usage
            exit $EXIT_CODE_SUCCESS
            ;;
        x64)    buildArch="x64" ;;
        x86)    buildArch="x86" ;;
        arm)    buildArch="arm" ;;
        arm64)  buildArch="arm64" ;;
        loongarch64) buildArch="loongarch64" ;;
        riscv64) buildArch="riscv64" ;;
        wasm)   buildArch="wasm" ;;
        android) buildOS="android" ;;
        wasi)   buildOS="wasi" ;;
        debug)  buildConfiguration="Debug" ;;
        checked) buildConfiguration="Checked" ;;
        release) buildConfiguration="Release" ;;
        *)      __positionalMatch=0 ;;
    esac

    if [[ "$__positionalMatch" -eq 1 ]]; then
        shift
        continue
    fi

    # Phase 2: All named args require a prefix (-, --, /)
    if [[ "$__origArg" != -* && "$__origArg" != /* ]]; then
        echo "Warning: Unrecognized argument '$1', treating as positional CORE_ROOT candidate."
        __lastPositional="$1"
        shift
        continue
    fi

    case "$__normArg" in
        v|verbose)
            verbose=1
            ;;
        a|arch)
            if [[ -n "$__embeddedValue" ]]; then
                buildArch="$(echo "$__embeddedValue" | tr '[:upper:]' '[:lower:]')"
            else
                shift
                buildArch="$(echo "$1" | tr '[:upper:]' '[:lower:]')"
            fi
            ;;
        c|configuration)
            local __cfgVal
            if [[ -n "$__embeddedValue" ]]; then
                __cfgVal="$__embeddedValue"
            else
                shift
                __cfgVal="$1"
            fi
            case "$(echo "$__cfgVal" | tr '[:upper:]' '[:lower:]')" in
                debug)   buildConfiguration="Debug" ;;
                release) buildConfiguration="Release" ;;
                checked) buildConfiguration="Checked" ;;
                *)       buildConfiguration="$__cfgVal" ;;
            esac
            ;;
        printlastresultsonly)
            printLastResultsOnly=1
            ;;
        jitstress)
            if [[ -n "$__embeddedValue" ]]; then
                export DOTNET_JitStress="$__embeddedValue"
            else
                shift
                export DOTNET_JitStress="$1"
            fi
            ;;
        jitstressregs)
            if [[ -n "$__embeddedValue" ]]; then
                export DOTNET_JitStressRegs="$__embeddedValue"
            else
                shift
                export DOTNET_JitStressRegs="$1"
            fi
            ;;
        jitminopts)
            export DOTNET_JITMinOpts=1
            ;;
        jitforcerelocs)
            export DOTNET_ForceRelocs=1
            ;;
        ilasmroundtrip)
            ((ilasmroundtrip = 1))
            ;;
        testrootdir)
            if [[ -n "$__embeddedValue" ]]; then
                testRootDir="$__embeddedValue"
            else
                shift
                testRootDir="$1"
            fi
            ;;
        corerootdir)
            if [[ -n "$__embeddedValue" ]]; then
                coreRootDir="$__embeddedValue"
            else
                shift
                coreRootDir="$1"
            fi
            ;;
        logsdir)
            if [[ -n "$__embeddedValue" ]]; then
                logsDir="$__embeddedValue"
            else
                shift
                logsDir="$1"
            fi
            ;;
        enableeventlogging)
            export DOTNET_EnableEventLog=1
            ;;
        runcrossgen2tests)
            export RunCrossGen2=1
            ;;
        synthesizepgo)
            export CrossGen2SynthesizePgo=1
            ;;
        sequential)
            runSequential=1
            ;;
        useservergc)
            export DOTNET_gcServer=1
            ;;
        longgc|long-gc)
            ((longgc = 1))
            ;;
        gcsimulator)
            ((gcsimulator = 1))
            ;;
        test-env|testenv)
            if [[ -n "$__embeddedValue" ]]; then
                testEnv="$__embeddedValue"
            else
                shift
                testEnv="$1"
            fi
            ;;
        gcstresslevel)
            if [[ -n "$__embeddedValue" ]]; then
                export DOTNET_GCStress="$__embeddedValue"
            else
                shift
                export DOTNET_GCStress="$1"
            fi
            __TestTimeout=1800000
            ;;
        gcname)
            if [[ -n "$__embeddedValue" ]]; then
                export DOTNET_GCName="$__embeddedValue"
            else
                shift
                export DOTNET_GCName="$1"
            fi
            ;;
        limiteddumpgeneration)
            limitedCoreDumps=ON
            ;;
        runincontext)
            runincontext=1
            ;;
        tieringtest)
            tieringtest=1
            ;;
        runnativeaottests)
            nativeaottest=1
            ;;
        interpreter)
            export RunInterpreter=1
            ;;
        node)
            export RunWithNodeJS=1
            ;;
        timeout)
            if [[ -n "$__embeddedValue" ]]; then
                __TestTimeout="$__embeddedValue"
            else
                shift
                __TestTimeout="$1"
            fi
            ;;
        parallel)
            if [[ -n "$__embeddedValue" ]]; then
                __ParallelType="$__embeddedValue"
            else
                shift
                __ParallelType="$1"
            fi
            ;;
        *)
            echo "Warning: Unrecognized argument '$1', treating as positional CORE_ROOT candidate."
            __lastPositional="$1"
            ;;
    esac
    shift
done

# If CORE_ROOT was specified as a positional argument and not via --coreRootDir
if [[ -z "$coreRootDir" && -n "$__lastPositional" ]]; then
    coreRootDir="$__lastPositional"
fi

# Set default for RunWithNodeJS when using wasm architecture
if [ "$buildArch" = "wasm" ] && [ -z "$RunWithNodeJS" ]; then
    export RunWithNodeJS=1
fi

################################################################################
# Call run.py to run tests.
################################################################################

runtestPyArguments=("-arch" "${buildArch}" "-build_type" "${buildConfiguration}")

echo "Build Architecture            : ${buildArch}"
echo "Build Configuration           : ${buildConfiguration}"

if [ "$buildArch" = "wasm" -a -z "$buildOS" ]; then
    buildOS="browser"
fi

if [ -n "$buildOS" ]; then
    runtestPyArguments+=("-os" "$buildOS")
fi

if [ "$buildOS" = "android" ]; then
    runtestPyArguments+=("-os" "android")
fi

if [[ -n "$testRootDir" ]]; then
    runtestPyArguments+=("-test_location" "$testRootDir")
    echo "Test Location                 : ${testRootDir}"
fi

if [[ -n "$coreRootDir" ]]; then
    runtestPyArguments+=("-core_root" "$coreRootDir")
    echo "CORE_ROOT                     : ${coreRootDir}"
fi

if [[ -n "$logsDir" ]]; then
    runtestPyArguments+=("-logs_dir" "$logsDir")
    echo "Logs directory                : ${logsDir}"
fi

if [[ -n "${testEnv}" ]]; then
    runtestPyArguments+=("-test_env" "${testEnv}")
    echo "Test Env                      : ${testEnv}"
fi

echo ""

if [[ -n "$longgc" ]]; then
    echo "Running Long GC tests"
    runtestPyArguments+=("--long_gc")
fi

if [[ -n "$gcsimulator" ]]; then
    echo "Running GC simulator tests"
    runtestPyArguments+=("--gcsimulator")
fi

if [[ -n "$ilasmroundtrip" ]]; then
    echo "Running Ilasm round trip"
    runtestPyArguments+=("--ilasmroundtrip")
fi

if (($verbose!=0)); then
    runtestPyArguments+=("--verbose")
fi

if [ "$runSequential" -ne 0 ]; then
    echo "Run tests sequentially."
    runtestPyArguments+=("--sequential")
fi

if [[ -n "$printLastResultsOnly" ]]; then
    runtestPyArguments+=("--analyze_results_only")
fi

if [[ -n "$RunCrossGen2" ]]; then
    runtestPyArguments+=("--run_crossgen2_tests")
fi

if [[ -n "$CrossGen2SynthesizePgo" ]]; then
    runtestPyArguments+=("--synthesize_pgo")
fi

if [[ "$limitedCoreDumps" == "ON" ]]; then
    runtestPyArguments+=("--limited_core_dumps")
fi

if [[ "$runincontext" -ne 0 ]]; then
    echo "Running in an unloadable AssemblyLoadContext"
    runtestPyArguments+=("--run_in_context")
fi

if [[ "$tieringtest" -ne 0 ]]; then
    echo "Running to encourage tier1 rejitting"
    runtestPyArguments+=("--tieringtest")
fi

if [[ "$nativeaottest" -ne 0 ]]; then
    echo "Running NativeAOT compiled tests"
    runtestPyArguments+=("--run_nativeaot_tests")
fi

if [[ -n "$RunInterpreter" ]]; then
    echo "Running tests with the interpreter"
    runtestPyArguments+=("--interpreter")
fi

if [[ -n "$RunWithNodeJS" ]]; then
    echo "Running tests with NodeJS"
    runtestPyArguments+=("--node")
fi

if [[ -n "$__TestTimeout" ]]; then
    runtestPyArguments+=("--test_timeout" "$__TestTimeout")
fi

if [[ -n "$__ParallelType" ]]; then
    runtestPyArguments+=("-parallel" "$__ParallelType")
fi

# Default to python3 if it is installed
__Python=python
if command -v python3 &>/dev/null; then
    __Python=python3
fi

# Run the tests using cross platform run.py
echo "$__Python $repoRootDir/src/tests/run.py ${runtestPyArguments[@]}"
$__Python "$repoRootDir/src/tests/run.py" "${runtestPyArguments[@]}"
exit "$?"
