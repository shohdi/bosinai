export const BosinaiContext = async () => ({
  "chat.params": async ({ model }, parameters) => {
    if (model.providerID === "bosinai") {
      parameters.maxOutputTokens = undefined;
    }
  },
});
