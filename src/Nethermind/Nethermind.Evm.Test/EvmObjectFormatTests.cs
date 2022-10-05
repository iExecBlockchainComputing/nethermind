//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Specs.Forks;
using NSubstitute;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Blockchain;
using static Nethermind.Evm.CodeAnalysis.ByteCodeValidator;
using NUnit.Framework.Constraints;
using System;
using System.Collections;
using DotNetty.Common.Utilities;
using Org.BouncyCastle.Asn1.Utilities;
using static Nethermind.Evm.Test.Eip3541Tests;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EvmObjectFormatTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiBlockNumber;
        protected override ISpecProvider SpecProvider
        {
            get
            {
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Is<long>(x => x >= BlockNumber)).Returns(Shanghai.Instance);
                specProvider.GetSpec(Arg.Is<long>(x => x < BlockNumber)).Returns(GrayGlacier.Instance);
                return specProvider;
            }
        }
        byte[] EofBytecode(byte[] bytecode, byte[] data = null)
        {
            var bytes = new byte[(data is not null && data.Length > 0 ? 10 + data.Length : 7) + bytecode.Length];

            int i = 0;

            // set magic
            bytes[i++] = 0xEF; bytes[i++] = 0x00; bytes[i++] = 0x01;

            // set code section
            var lenBytes = bytecode.Length.ToByteArray();
            bytes[i++] = 0x01; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];

            // set PushData section
            if (data is not null && data.Length > 0)
            {
                lenBytes = data.Length.ToByteArray();
                bytes[i++] = 0x02; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
            }
            bytes[i++] = 0x00;

            // set the terminator byte
            Array.Copy(bytecode, 0, bytes, i, bytecode.Length);
            if (data is not null && data.Length > 0)
            {
                Array.Copy(data, 0, bytes, i + bytecode.Length, data.Length);
            }

            return bytes;
        }

        // valid code
        [TestCase("0xEF00010100010000", true, 1, 0, true)]
        [TestCase("0xEF0001010002006000", true, 2, 0, true)]
        [TestCase("0xEF0001010002020001006000AA", true, 2, 1, true)]
        [TestCase("0xEF0001010002020004006000AABBCCDD", true, 2, 4, true)]
        [TestCase("0xEF00010100040200020060006001AABB", true, 4, 2, true)]
        [TestCase("0xEF000101000602000400600060016002AABBCCDD", true, 6, 4, true)]
        // code with invalid magic will NOT fail following EIP-3541 or EIP-3540 
        [TestCase("", true, 0, 0, true, Description = "Empty code")]
        [TestCase("0xFE", true, 0, 0, true, Description = "Codes starting with invalid magic first byte")]
        [TestCase("0xFE0001010002020004006000AABBCCDD", true, 0, 0, true, Description = "Valid code with wrong magic first byte")]
        // code with invalid magic will fail following EIP-3541
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete Magic")]
        [TestCase("0xEF01", false, 0, 0, true, Description = "Incorrect Magic second byte")]
        [TestCase("0xEF0101010002020004006000AABBCCDD", false, 0, 0, true, Description = "Valid code with wrong magic second byte")]
        // code with valid magic but invalid body
        [TestCase("0xEF0000010002020004006000AABBCCDD", false, 0, 0, true, Description = "Invalid Version")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section missing")]
        [TestCase("0xEF0001010002006000DEADBEEF", false, 0, 0, true, Description = "Invalid total Size")]
        [TestCase("0xEF00010100020100020060006000", false, 0, 0, true, Description = "Multiple Code sections")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000102000401000200AABBCCDD6000", false, 0, 0, true, Description = "PushData section before code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "PushData Section size Missing")]
        [TestCase("0xEF0001010002020004020004006000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple PushData sections")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown Section")]
        public void EOF_Compliant_formats_Test(string code, bool isCorrectFormated, int codeSize, int dataSize, bool isShanghaiFork)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            IReleaseSpec spec = isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance;

            var expectedHeader = codeSize == 0 && dataSize == 0
                ? null
                : new EofHeader
                {
                    CodeSize = (ushort)codeSize,
                    DataSize = (ushort)dataSize
                };
            var checkResult = ValidateByteCode(bytecode, spec, out var header);

            if (isShanghaiFork)
            {
                header.Should().Be(expectedHeader);
                checkResult.Should().Be(isCorrectFormated);
            }
            else
            {
                checkResult.Should().Be(isCorrectFormated);
            }
        }

        public class TestCase
        {
            public byte[] Code;
            public byte[] Data;
            public (byte Status, string error) ExpectedResult;
            public string Description;
        }

        public static IEnumerable<TestCase> Eip3540TestCases
        {
            get
            {
                /*
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(0x2a)
                            .PushData(0x2b)
                            .Op(Instruction.MSTORE8)
                            .Op(Instruction.MSIZE)
                            .PushData(0x0)
                            .Op(Instruction.SSTORE)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.STOP)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Return(0, 1)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.PC)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include PC instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.PC)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include PC instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include JUMP instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Include JUMP instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include JUMPI instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Include JUMPI instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Failure, "InvalidJumpDestination"),
                    Description = "EOF1 execution with data section: Try to jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Failure, "InvalidJumpDestination"),
                    Description = "EOF1 execution : Try to conditinally jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data(new byte[(int)Instruction.PUSH3])
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Push in header (Data count is 0x62 which is PUSH3))"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.CODESIZE)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include CODESIZE"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.CODESIZE)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODESIZE"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(19)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(19, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies full code"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(26)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(26, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section : Includes CODECOPY, copies full code"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(10)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(10, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section : Includes CODECOPY, copies header"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(7)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(7, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies header"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(12)
                            .PushData(7)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(12, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies code section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(12)
                            .PushData(10)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(12, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODECOPY copies code section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .PushData(22)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(4, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODECOPY copies Data section"
                };



                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(30)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(23, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: copies Data out of bound (result is 0 padded)"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(23)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(23, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : copies Data out of bound (result is 0 padded)"
                };
                */

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(new byte[] { 23, 69})
                            .Return(2, 0)
                            .Done,
                    ExpectedResult = (StatusCode.Success, null),
                    Description = "EOF1 execution : includes PUSHx Intructions"
                };
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip3540TestCases))] TestCase testcase)
        {
            var bytecode =
                EofBytecode(
                    testcase.Code,
                    testcase.Data
                );

            if (IsEOFCode(bytecode, out _))
            {
                TestAllTracerWithOutput receipts = Execute(BlockNumber, Int64.MaxValue, bytecode, Int64.MaxValue);
                var message = receipts.StatusCode == StatusCode.Success ? $"returns : {receipts.ReturnValue}" : $"failed : {receipts.Error}";
                receipts.StatusCode.Should().Be(testcase.ExpectedResult.Status, message);
                receipts.Error.Should().Be(testcase.ExpectedResult.error, message);
            }
        }
    }
}
