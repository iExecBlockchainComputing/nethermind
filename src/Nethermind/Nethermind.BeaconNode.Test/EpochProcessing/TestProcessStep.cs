// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconNode.Test.EpochProcessing
{
    public enum TestProcessStep
    {
        None = 0,
        ProcessJustificationAndFinalization,
        //ProcessCrosslinks,
        ProcessRewardsAndPenalties,
        ProcessRegistryUpdates,
        ProcessSlashings,
        ProcessFinalUpdates,
    }
}