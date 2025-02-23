using Microsoft.Kiota.Abstractions.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Kiota.Builder.SearchProviders.GitHub.GitHubClient.Models {
    public class FileCommit_commit : IAdditionalDataHolder, IParsable {
        /// <summary>Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.</summary>
        public IDictionary<string, object> AdditionalData { get; set; }
        /// <summary>The author property</summary>
        public FileCommit_commit_author Author { get; set; }
        /// <summary>The committer property</summary>
        public FileCommit_commit_committer Committer { get; set; }
        /// <summary>The html_url property</summary>
        public string Html_url { get; set; }
        /// <summary>The message property</summary>
        public string Message { get; set; }
        /// <summary>The node_id property</summary>
        public string Node_id { get; set; }
        /// <summary>The parents property</summary>
        public List<FileCommit_commit_parents> Parents { get; set; }
        /// <summary>The sha property</summary>
        public string Sha { get; set; }
        /// <summary>The tree property</summary>
        public FileCommit_commit_tree Tree { get; set; }
        /// <summary>The url property</summary>
        public string Url { get; set; }
        /// <summary>The verification property</summary>
        public FileCommit_commit_verification Verification { get; set; }
        /// <summary>
        /// Instantiates a new FileCommit_commit and sets the default values.
        /// </summary>
        public FileCommit_commit() {
            AdditionalData = new Dictionary<string, object>();
        }
        /// <summary>
        /// Creates a new instance of the appropriate class based on discriminator value
        /// </summary>
        /// <param name="parseNode">The parse node to use to read the discriminator value and create the object</param>
        public static FileCommit_commit CreateFromDiscriminatorValue(IParseNode parseNode) {
            _ = parseNode ?? throw new ArgumentNullException(nameof(parseNode));
            return new FileCommit_commit();
        }
        /// <summary>
        /// The deserialization information for the current model
        /// </summary>
        public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() {
            return new Dictionary<string, Action<IParseNode>> {
                {"author", n => { Author = n.GetObjectValue<FileCommit_commit_author>(FileCommit_commit_author.CreateFromDiscriminatorValue); } },
                {"committer", n => { Committer = n.GetObjectValue<FileCommit_commit_committer>(FileCommit_commit_committer.CreateFromDiscriminatorValue); } },
                {"html_url", n => { Html_url = n.GetStringValue(); } },
                {"message", n => { Message = n.GetStringValue(); } },
                {"node_id", n => { Node_id = n.GetStringValue(); } },
                {"parents", n => { Parents = n.GetCollectionOfObjectValues<FileCommit_commit_parents>(FileCommit_commit_parents.CreateFromDiscriminatorValue)?.ToList(); } },
                {"sha", n => { Sha = n.GetStringValue(); } },
                {"tree", n => { Tree = n.GetObjectValue<FileCommit_commit_tree>(FileCommit_commit_tree.CreateFromDiscriminatorValue); } },
                {"url", n => { Url = n.GetStringValue(); } },
                {"verification", n => { Verification = n.GetObjectValue<FileCommit_commit_verification>(FileCommit_commit_verification.CreateFromDiscriminatorValue); } },
            };
        }
        /// <summary>
        /// Serializes information the current object
        /// </summary>
        /// <param name="writer">Serialization writer to use to serialize this model</param>
        public void Serialize(ISerializationWriter writer) {
            _ = writer ?? throw new ArgumentNullException(nameof(writer));
            writer.WriteObjectValue<FileCommit_commit_author>("author", Author);
            writer.WriteObjectValue<FileCommit_commit_committer>("committer", Committer);
            writer.WriteStringValue("html_url", Html_url);
            writer.WriteStringValue("message", Message);
            writer.WriteStringValue("node_id", Node_id);
            writer.WriteCollectionOfObjectValues<FileCommit_commit_parents>("parents", Parents);
            writer.WriteStringValue("sha", Sha);
            writer.WriteObjectValue<FileCommit_commit_tree>("tree", Tree);
            writer.WriteStringValue("url", Url);
            writer.WriteObjectValue<FileCommit_commit_verification>("verification", Verification);
            writer.WriteAdditionalData(AdditionalData);
        }
    }
}
