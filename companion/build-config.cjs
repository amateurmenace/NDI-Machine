// Preserve the transport's runtime file paths. Bundling threadedclass/ATEM into
// one file loses the socket implementation path used even in single-thread mode.
module.exports = { externals: ['atem-connection'] }
