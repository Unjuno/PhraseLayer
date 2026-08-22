using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OpusMtOnnxExportMetadataTests
    {
        [Fact]
        public void ReferenceRuntimeUsesNonCachedThreeInputDecoder()
        {
            Assert.Equal("encoder_model.onnx", OpusMtEnJapMeasuredOnnxContract.Encoder.FileName);
            Assert.Equal("decoder_model.onnx", OpusMtEnJapMeasuredOnnxContract.Decoder.FileName);
            Assert.Equal(2, OpusMtEnJapMeasuredOnnxContract.Encoder.Inputs.Count);
            Assert.Equal(3, OpusMtEnJapMeasuredOnnxContract.Decoder.Inputs.Count);
            Assert.Equal("encoder_attention_mask", OpusMtEnJapMeasuredOnnxContract.Decoder.Inputs[0].Name);
            Assert.Equal("input_ids", OpusMtEnJapMeasuredOnnxContract.Decoder.Inputs[1].Name);
            Assert.Equal("encoder_hidden_states", OpusMtEnJapMeasuredOnnxContract.Decoder.Inputs[2].Name);
        }

        [Fact]
        public void MeasuredShapesLockHiddenAndVocabularyDimensions()
        {
            Assert.Equal(OpusMtEnJapMeasuredOnnxContract.HiddenSize.ToString(),
                OpusMtEnJapMeasuredOnnxContract.Encoder.Outputs[0].Dimensions[2]);
            Assert.Equal(OpusMtEnJapMeasuredOnnxContract.VocabularySize.ToString(),
                OpusMtEnJapMeasuredOnnxContract.Decoder.Outputs[0].Dimensions[2]);
            Assert.Equal("last_hidden_state", OpusMtEnJapMeasuredOnnxContract.Encoder.Outputs[0].Name);
            Assert.Equal("logits", OpusMtEnJapMeasuredOnnxContract.Decoder.Outputs[0].Name);
        }

        [Fact]
        public void ReferenceRuntimeSizeEqualsMeasuredEncoderPlusDecoder()
        {
            Assert.Equal(
                OpusMtEnJapMeasuredOnnxContract.ReferenceRuntimeSizeBytes,
                OpusMtEnJapMeasuredOnnxContract.Encoder.SizeBytes +
                OpusMtEnJapMeasuredOnnxContract.Decoder.SizeBytes);
            Assert.Equal(18, OpusMtEnJapMeasuredOnnxContract.Encoder.Opset);
            Assert.Equal(18, OpusMtEnJapMeasuredOnnxContract.Decoder.Opset);
        }
    }
}
