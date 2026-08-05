using System;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-2-2 纯单元测试入口。
    /// 蓝图 §3：测试入口仅返回 0（全过）或非 0（失败）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.WriteLine("=== SteamP2PFriends.WhitelistTests (Stage 7-2-2) ===");

            int total = 0;
            int passed = 0;
            int failed = 0;

            RunTest("1. Bootstrap_Success", WhitelistServiceTests.Test_Bootstrap_Success, ref total, ref passed, ref failed);
            RunTest("2a. Bootstrap_SaveFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_SaveFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("2b. Bootstrap_LoadFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_LoadFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("2c. Bootstrap_ContainsFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_ContainsFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("3a. Add_SaveFailure_GatewayOnce", WhitelistServiceTests.Test_Add_SaveFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3b. Add_LoadFailure_GatewayOnce", WhitelistServiceTests.Test_Add_LoadFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3c. Add_ContainsFailure_GatewayOnce", WhitelistServiceTests.Test_Add_ContainsFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3d. Add_SnapshotFailure_GatewayOnce", WhitelistServiceTests.Test_Add_SnapshotFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("4a. Remove_SaveFailure_GatewayOnce", WhitelistServiceTests.Test_Remove_SaveFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("4b. Remove_NoOp_NoSave_NoDisconnect", WhitelistServiceTests.Test_Remove_NoOp_NoSave_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("4c. Remove_SnapshotFailure_GatewayOnce", WhitelistServiceTests.Test_Remove_SnapshotFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("5a. Add_Self_Rejected", WhitelistServiceTests.Test_Add_Self_Rejected, ref total, ref passed, ref failed);
            RunTest("5b. Remove_Self_Rejected", WhitelistServiceTests.Test_Remove_Self_Rejected, ref total, ref passed, ref failed);
            RunTest("5c. Add_InvalidLocalUser_Rejected", WhitelistServiceTests.Test_Add_InvalidLocalUser_Rejected, ref total, ref passed, ref failed);
            RunTest("5d. Remove_InvalidLocalUser_Rejected", WhitelistServiceTests.Test_Remove_InvalidLocalUser_Rejected, ref total, ref passed, ref failed);
            RunTest("6. Add_JudgeId_Equals_LocalUser", WhitelistServiceTests.Test_Add_JudgeId_Equals_LocalUser, ref total, ref passed, ref failed);
            RunTest("7. PersistenceFault_Blocks_Second_Mutate_And_Reset_Restores", WhitelistServiceTests.Test_PersistenceFault_Blocks_Second_Mutate_And_Reset_Restores, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine($"=== Total: {total} / Passed: {passed} / Failed: {failed} ===");

            return failed == 0 ? 0 : 1;
        }

        private static void RunTest(string name, Func<bool> test, ref int total, ref int passed, ref int failed)
        {
            total++;
            Console.Write($"[{total,2}] {name,-60} ... ");
            try
            {
                bool ok = test();
                if (ok)
                {
                    passed++;
                    Console.WriteLine("PASS");
                }
                else
                {
                    failed++;
                    // 失败原因已在 test 内打印
                    Console.WriteLine("FAIL");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL (exception)");
                Console.WriteLine("    " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
