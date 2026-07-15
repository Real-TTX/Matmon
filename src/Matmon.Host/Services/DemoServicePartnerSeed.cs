using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>Dev/preview only: a dummy managing service partner (logo + accent + contact), seeded when
/// <see cref="MatmonRuntimeOptions.DemoServicePartner"/> is set, so reseller co-branding is visible on this
/// instance without a real Matmon.Cloud link. Off in production (the flag defaults false).</summary>
internal static class DemoServicePartnerSeed
{
    public static ServicePartnerInfo Build() => new()
    {
        HasPartner = true,
        Name = "ACME Managed Services",
        ContactEmail = "support@acme-msp.example",
        ContactPhone = "+49 30 1234567",
        CanManage = true,
        BrandingSuppressed = false,
        ContactUrl = "https://acme-msp.example/support",
        BrandColor = "#7C3AED",
        LogoContentType = "image/png",
        LogoPng = Convert.FromBase64String(LogoBase64),
    };

    // A small embedded PNG (purple "ACME MSP" badge) so the demo logo needs no container fonts or cloud fetch.
    private const string LogoBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAARgAAABACAYAAADBJGiiAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAfYSURBVHhe7Zy/i1xVFMdfaemfYGlp586kSEAQm1QWYmUsBMHGSuLO6kYJ+AMFU2i0CDamEJEgQQMKKlgsiAQEbVKYkCKgO5NZ3SSbZBNHvhPvcuf7zn3vvnnnLkG/X/gQ5t0f58y+e8798V6mqhq0Ovz9kbXh+MhoMDkmhBAM8gPyBOeOpF4ebD48Gk7OrA0nMyGEyGd8bvXAeIVzyp7+Xa3s1BsKIUQ2b6wfvPjAQnJZHW4+b1QUQojuDK6e3Esu822RVi5CCEdGBzYPzRPM2nDyHRcKIUQ/xhcr7JXqBUII0Z8Kp758UQghPKhWV8Yv8kUhhPCguveyTL1ACCH6ogQjhCiGEowQohhKMEKIYijBCCGKoQQjhCiGEowQohhKMEKIYijBCCGKoQQjhCiGEowQohhKMEKIYijBCCGKsW8J5sTTW7NTL/y1x/vPbNXqCCH+WxRPMF+duD7749KdmaXplbuzb0/dmB07dLXWrgRn371e4/XHlrf9wbN/zj57bXuhv09e2p5f57q4zraBVZd576mtWrtgi+um7Fhw2xSpPr19j3nr8HQ+EXE72EQZ10/5GMB9yvFX+FIswWDFcvmXXc4pppCAPnqu7M3HALSEgcd1m8Dg/uH0DndT06/f31poh8+WLv28W7PBpOyxjSY7lrhtilSf3r4DJIGUvVhIGjk+WuK2ohxFEsznx6/Ndm/9zfe1VV+fLHfjfzp7k83NlRMkgVSSssQB1BQA1owcwAorJbbRZofFbVM09enpe5e/LyeJJh8tdbnvYnncEwxWLssklyAsi7nPvjQNdKgpSAKpwY+BGi/Fw0DnAGoKgKZVVMouxDYsO/jM24UAt03Bfcby8h33wBImhuAvVkP4e0PsP/sYf+/U5NK2TRP9cU8wuduilLbHd2fHH1/+XMQCQRCLByMPVgbLdhYGetOensvYZqym2TQODu6Dg9Sy0/bdcuA+Y3n5/s2HNxbKIZzfcJ8AyYjLuH/+3qjPYh+EP64JBge6Hjr/5c1a330Is14QJ4zx5Tu1NjEo5/pdD4c5AHhWtVZRvPLi8wwrQNgOB9oycJ8lfGcbXN4Gt7e+N/sAcR3hi2uCwVMhD2GL5bWK4aU3ggPXOWnwjBiwlvm8OsmBA4BXVZjBuQ3b5lneCkK2YwVaV7jPEr6zDSh1Tyy4vfW9cY3FdYQvbgkG77V46vRRn/0xD+xwZsDXrSABPOu1rXZScABw4rP6jVcK4SwiFgepZccKtK5wnyV85/sRlHtOwj5a35vvJcR1hC9uCebTV+szfR95PVHilUpYzuMwOZYVJFZ7DFKukwMHAK7x1i2esXmLEd7ziMVBatnB53DYGZMbuFafuObtOyetWLgHaN+0LWUfOcFYZzDL3kuRj1uC+eLta3z/egkv4LGNrvBZCx9IsqwnWCweuLlwAOCatY0I9bkMwdUWpJadlKy2KbhPyz8P37mdpdTfn32MEyufGQVZZ0fCF7cEg3dfPOWRYHhJzI9Uudya0VipAd4GBwCu8Uwfr6Li+uHcKCdI2U5KVtsU3CeulfAdYKXB9lgo59VMWxtWlxWcWB63BIM3cT2FLRfb6AoLCSTeJlgzW1sfqbOaNjgAwnX2AQHG24WwssoJUrYTz+QxXQKM+wzXvX2PweqTJ4BYfB/Yx5Tgc5fDY9EPtwSD/0+0s738C3asd57st3zNWW5b4sDjswbeZuXCARCus58IHH5KE+rmBCnbWXbF1dRnuO7tuwUSFieyoCYfObEi0WlLtP+4JRjw4xl7IHTVlQv2gWsXUoOyTWFJH7Cebng8pg7XeauBBBb7Hm/bcoKU7ZRMMN6+N2Hdz/g+sI8e31v0xzXBvHl4On8Tt4/wDkzfn3LggQ/x9ihgDdx4f289fUAg8RlAGxwAcVnTViA+eM4JUrbjEWjcZ1zm5XvbtoUP7CElmPsf1wQD8P5KH3k8nuZlOpRKCHxmAPE2iQcvhCSTWnLDFq9yuI+4jB+Zx4rrtQWpZccj0LjPuMzLd3xGskr9Ta2JgNvH8vjeoj/uCQYsu1W6sHG71tcy8LkJb3sYrs+D31oRBaHveEUUBjr3wQHAPljip1ptQWrZwWdetQU4CabgPrncUlffYxu4H9iaog3+5XeRIO6ffVSCuT8okmAAHlvnHvpiW+SxcgE5KxLGWvHwTIrPnIia1BRAEPtgbTX4vZy2ILXsNCk3CLlPLvfwnW00ydqicvvc7ybKUizBAJzJ/Hb+9sKNZ3n/2JR1KMuDkbGSEr8zE8DAtWZUVtsMy/1aZz1cpy1ILTtNyg1C7pPLPXzn8pRQz7qf7GPudxNlKZpgAnjkjDd98fJcAP/zGr8dw3X7El5LD7StXgLWT19ynRgEFdsKP8toBQDX5XLQZh99t9VhO03kbpG4Ty4HbX7l+A6a/q5cN4bbtNUX+8O+JBghxP8TJRghRDGUYIQQxVCCEUIUQwlGCFEMJRghRDGUYIQQxVCCEUIUQwlGCFEMJRghRDGUYIQQxVCCEUIUQwlGCFGMajTYPMoXhRDCg2q0MnmCLwohhAfV+sHpg3xRCCE8qKC14fgiFwghRB9Gg8nOPMFomySE8Abnu/MEA70ymHzMFYQQYikGk4295ALdO4sZn6tVFEKILgwmG+uPTh9aSDBBa8PxkdFgPK01EkKIBnDmsrAtqqrqHyBXXxrlm1+QAAAAAElFTkSuQmCC";
}
