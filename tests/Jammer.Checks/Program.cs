if (!args.Contains("--readiness")) await ConnectionCheck.Run();
await ReadinessCheck.Run();
