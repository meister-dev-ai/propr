// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class AzureDevOpsApplicationBoundaryTests
{
    [Fact]
    public void ApplicationAssembly_ContainsNoConcreteScmProviderTypesOrMembers()
    {
        var exportedAzureSpecificInterfaces = typeof(IReviewAssignmentService).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMembers(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.DeclaredOnly)
                .SelectMany(member => new[] { $"{type.FullName}.{member.Name}" }.Concat(
                    member is System.Reflection.MethodBase method
                        ? method.GetParameters().Select(parameter => $"{type.FullName}.{member.Name}.{parameter.Name}")
                        : []))
                .Append(type.FullName!))
            .Where(IsConcreteScmName)
            .OrderBy(static typeName => typeName, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(exportedAzureSpecificInterfaces);
    }

    [Fact]
    public void ApplicationSignatures_DoNotExposeNativeOrBrandedSharedContracts()
    {
        Assert.Empty(FindSignatureViolations(typeof(IReviewAssignmentService).Assembly.GetTypes()));
    }

    internal static string[] FindSignatureViolations(IEnumerable<Type> roots)
    {
        var pending = new Stack<Type>(roots);
        var visited = new HashSet<Type>();
        var violations = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var type))
        {
            if (!visited.Add(type))
            {
                continue;
            }

            var name = type.FullName ?? type.Name;
            if (IsConcreteScmName(name))
            {
                violations.Add(name);
            }

            if (type.HasElementType)
            {
                pending.Push(type.GetElementType()!);
            }

            foreach (var argument in type.GetGenericArguments())
            {
                pending.Push(argument);
            }

            if (type.IsGenericParameter)
            {
                foreach (var constraint in type.GetGenericParameterConstraints())
                {
                    pending.Push(constraint);
                }

                continue;
            }

            // Provider enum values are normalized data; framework implementations are outside this contract graph.
            if (type.IsEnum || !IsFirstPartyContract(type))
            {
                continue;
            }

            if (type.BaseType is { } baseType)
            {
                pending.Push(baseType);
            }

            foreach (var contract in type.GetInterfaces())
            {
                pending.Push(contract);
            }

            foreach (var member in type.GetMembers(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                var memberName = $"{name}.{member.Name}";
                if (IsConcreteScmName(memberName))
                {
                    violations.Add(memberName);
                }

                if (member is System.Reflection.MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var parameterName = $"{memberName}.{parameter.Name}";
                        if (IsConcreteScmName(parameterName))
                        {
                            violations.Add(parameterName);
                        }
                    }
                }

                foreach (var dependency in GetMemberTypes(member))
                {
                    pending.Push(dependency);
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsFirstPartyContract(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("MeisterDev.ProPR.", StringComparison.Ordinal) == true;

    private static IEnumerable<Type> GetMemberTypes(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType).Concat(method.GetGenericArguments()),
        System.Reflection.ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        System.Reflection.PropertyInfo property => [property.PropertyType],
        System.Reflection.FieldInfo field => [field.FieldType],
        System.Reflection.EventInfo eventInfo => [eventInfo.EventHandlerType!],
        Type nestedType => [nestedType],
        _ => Array.Empty<Type>(),
    };

    [Theory]
    [InlineData(typeof(ProviderBoundaryReflectionFixtures.IndirectEnvelope))]
    [InlineData(typeof(ProviderBoundaryReflectionFixtures.ConstrainedEnvelope<>))]
    [InlineData(typeof(ProviderBoundaryReflectionFixtures.ConstrainedOperation))]
    [InlineData(typeof(ProviderBoundaryReflectionFixtures.DerivedEnvelope))]
    [InlineData(typeof(ProviderBoundaryReflectionFixtures.IForwardedEnvelope))]
    public void ReflectionGuardDetectsNativeContractsBehindNeutralDeclarations(Type root)
    {
        Assert.Contains(typeof(IIdentityResolver).FullName!, FindSignatureViolations([root]));
    }

    [Fact]
    public void ReflectionGuardTerminatesOnCyclesAndAllowsCarriedProviderEnums()
    {
        Assert.Empty(FindSignatureViolations([typeof(ProviderBoundaryReflectionFixtures.CyclicEnvelope)]));
    }

    private static bool IsConcreteScmName(string name) =>
        name != "MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models.CandidateReviewFinding.GitHubActionsSecretEchoClaimKind" &&
        (name.Contains("AzureDevOps", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("GitHub", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("GitLab", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Forgejo", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("AdoOrganization", StringComparison.Ordinal) ||
         name.Contains("Microsoft.TeamFoundation", StringComparison.Ordinal) ||
         name.Contains("Microsoft.VisualStudio.Services", StringComparison.Ordinal) ||
         name.Split('.', '+', '<', '>').Any(part => part.StartsWith("Ado", StringComparison.Ordinal) || part.StartsWith("IAdo", StringComparison.Ordinal) ||
                                                    part is "IIdentityResolver" or "ResolvedIdentity"));
}
