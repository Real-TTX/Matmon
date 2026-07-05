using System;

// Test if Uri constructor can be tricked with path traversal
var baseUri = new Uri("http://localhost:8099/");
try {
    var target = new Uri(baseUri, "../../../../../../etc/passwd");
    Console.WriteLine($"Target: {target}");
} catch (Exception ex) {
    Console.WriteLine($"Error: {ex.Message}");
}

// Test with leading slashes
try {
    var target2 = new Uri(baseUri, "/api/admin");
    Console.WriteLine($"Target2: {target2}");
} catch (Exception ex) {
    Console.WriteLine($"Error2: {ex.Message}");
}

// Test with empty path
try {
    var target3 = new Uri(baseUri, "");
    Console.WriteLine($"Target3: {target3}");
} catch (Exception ex) {
    Console.WriteLine($"Error3: {ex.Message}");
}
