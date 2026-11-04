if (args.Contains("--proxy")) { await ProxyCheck.Run(); return; }
if (args.Contains("--event-wait")) { EventWaitCheck.Run(); return; }
if (!args.Contains("--readiness")) await ConnectionCheck.Run();
await ReadinessCheck.Run();
await ProxyCheck.Run();
EventWaitCheck.Run();
