// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using System.Reflection;
using System.Reflection.Emit;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Features.Clients.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class ExplicitProviderContextTests
{
    [Theory]
    [InlineData(typeof(ReviewJobProtocolDto))]
    [InlineData(typeof(ReviewJobStatusDto))]
    [InlineData(typeof(SubmitReviewJobRequestDto))]
    [InlineData(typeof(PullRequestSynchronizationRequest))]
    public void ApplicationInputsAndOutputsRequireCapturedProvider(Type type)
    {
        Assert.NotNull(type.GetProperty("Provider")!.GetCustomAttributes(typeof(RequiredMemberAttribute), false).SingleOrDefault());
    }

    [Theory]
    [InlineData(typeof(ReviewJob))]
    [InlineData(typeof(MentionReplyJob))]
    [InlineData(typeof(ThreadPassJob))]
    public void DomainJobsHaveAnExplicitReviewContextConstructionPath(Type type)
    {
        Assert.Contains(type.GetConstructors(), constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(CodeReviewRef)));
    }

    [Fact]
    public void ExplicitJobConstructionRetainsSyntheticSourceCoordinates()
    {
        var host = new ProviderHostRef((ScmProvider)792, "https://synthetic.example");
        var review = new CodeReviewRef(
            new RepositoryRef(host, "repo", "owner", "owner/project"),
            CodeReviewPlatformKind.PullRequest, "external-review", 42);
        var client = Guid.NewGuid();
        var reviewJob = new ReviewJob(
            review, Guid.NewGuid(), client, "https://synthetic.example/saved",
            "saved-project", "repo", 42, 7);
        var threadPass = new ThreadPassJob(
            review, Guid.NewGuid(), client, "https://synthetic.example/saved",
            "saved-project", "repo", 42, 7, "revision", "trigger");
        var mention = new MentionReplyJob(
            review, Guid.NewGuid(), client, "https://synthetic.example/saved",
            "saved-project", "repo", 42, "thread", 1, "question");

        Assert.Equal(review, reviewJob.CodeReviewReference);
        Assert.Equal(review, threadPass.CodeReviewReference);
        Assert.Equal(review, mention.CodeReviewReference);
        Assert.Equal("https://synthetic.example/saved", reviewJob.OrganizationUrl);
        Assert.Equal("saved-project", threadPass.ProjectId);
        Assert.Equal("thread", mention.ThreadId);
    }

    [Fact]
    public void ApplicationConstructsJobsWithExplicitCapturedSource()
    {
        AssertExplicitConstruction(typeof(SubmitReviewJobRequestDto).Assembly.GetTypes());
    }

    [Fact]
    public void ConstructorGuardAcceptsAdditionalExplicitProducer()
    {
        AssertExplicitConstruction(
            typeof(SubmitReviewJobRequestDto).Assembly.GetTypes()
                .Append(typeof(JobConstructionReflectionFixtures.AdditionalProducer)));
    }

    [Fact]
    public void ConstructorGuardRejectsMissingJobTypeCoverage()
    {
        Assert.NotNull(Record.Exception(() => AssertExplicitConstruction([typeof(JobConstructionReflectionFixtures.ReviewOnlyProducer)])));
    }

    [Fact]
    public void ConstructorGuardRejectsEmptyGraph()
    {
        Assert.NotNull(Record.Exception(() => AssertExplicitConstruction([])));
    }

    [Fact]
    public void ConstructorGuardRejectsImplicitInvocationEvenWithAllJobTypesPresent()
    {
        Assert.NotNull(Record.Exception(() => AssertExplicitConstruction([typeof(JobConstructionReflectionFixtures.MixedProducer)])));
    }

    private static void AssertExplicitConstruction(IEnumerable<Type> roots)
    {
        var jobs = new HashSet<Type> { typeof(ReviewJob), typeof(MentionReplyJob), typeof(ThreadPassJob) };
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => unchecked((ushort)opcode.Value));
        var observedJobs = new HashSet<Type>();
        foreach (var type in roots)
        {
            var methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
            foreach (var method in methods)
            {
                var bytes = method.GetMethodBody()?.GetILAsByteArray();
                if (bytes is null)
                {
                    continue;
                }

                for (var offset = 0; offset < bytes.Length;)
                {
                    ushort value = bytes[offset++];
                    if (value == 0xfe)
                    {
                        value = (ushort)(0xfe00 | bytes[offset++]);
                    }

                    var opcode = opcodes[value];
                    if (opcode == OpCodes.Newobj)
                    {
                        var constructor = (ConstructorInfo)method.Module.ResolveMethod(
                            BitConverter.ToInt32(bytes, offset), type.GetGenericArguments(),
                            method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                        if (jobs.Contains(constructor.DeclaringType!))
                        {
                            observedJobs.Add(constructor.DeclaringType!);
                            Assert.Contains(
                                constructor.GetParameters(), parameter =>
                                    parameter.ParameterType == typeof(CodeReviewRef) ||
                                    parameter.ParameterType == typeof(CodeReviewSourceContext));
                        }
                    }

                    offset += opcode.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(bytes, offset)),
                        _ => 4,
                    };
                }
            }
        }

        Assert.NotEmpty(observedJobs);
        Assert.All(jobs, job => Assert.Contains(job, observedJobs));
    }

    [Fact]
    public void AuthenticationCoordinatesAreTypedNeutralValues()
    {
        var assembly = typeof(ScmAuthenticationConfiguration).Assembly;
        var applicationId = assembly.GetType("MeisterDev.ProPR.Application.Features.Clients.Models.ScmApplicationId");
        var installationId = assembly.GetType("MeisterDev.ProPR.Application.Features.Clients.Models.ScmInstallationId");
        Assert.NotNull(applicationId);
        Assert.NotNull(installationId);
        Assert.True(applicationId.IsValueType);
        Assert.True(installationId.IsValueType);
        foreach (var type in new[] { typeof(ScmAuthenticationConfiguration), typeof(ClientScmConnectionDto), typeof(ClientScmConnectionCredentialDto) })
        {
            Assert.Equal(typeof(Nullable<>).MakeGenericType(applicationId), type.GetProperty("AppId")!.PropertyType);
            Assert.Equal(typeof(Nullable<>).MakeGenericType(installationId), type.GetProperty("InstallationId")!.PropertyType);
        }
    }

    [Fact]
    public void PatchUsesPreparedUserNameCoordinatesWithoutNativeAuthenticationDecisions()
    {
        var policy = Substitute.For<IScmConnectionConfigurationPolicy>();
        policy.PreparePatchAppMetadata(Arg.Any<ScmAuthenticationKind>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<long?>())
            .Returns(new ScmAppPatchMetadata(null, null, null, null));
        policy.PreparePatchUserName(ScmAuthenticationKind.PersonalAccessToken, "supplied", "supplied")
            .Returns(new ScmUserNamePatchMetadata("prepared candidate", "prepared persisted"));
        var existing = new ClientScmConnectionDto(
                Guid.NewGuid(), Guid.NewGuid(), (ScmProvider)792,
                "https://synthetic.example", ScmAuthenticationKind.PersonalAccessToken, "Connection", true,
                "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) with
            {
                UserName = "saved"
            };
        var result = ProviderConnectionConfigurationService.ResolvePatchAuthentication(policy, null, "supplied", null, null, null, null, false, existing);
        Assert.Equal("prepared candidate", result.CandidateUserName);
        Assert.Equal("prepared persisted", result.PersistedUserName);
    }
}
